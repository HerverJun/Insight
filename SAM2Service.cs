using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Point = System.Drawing.Point;

namespace Insight
{
    public class SAM2Service : IDisposable
    {
        private InferenceSession? _encoderSession;
        private InferenceSession? _decoderSession;

        // Cached Encoder Outputs
        private float[]? _imageEmbeddings;
        private float[]? _highResFeats0;
        private float[]? _highResFeats1;

        // Metadata
        private int[] _embeddingShape = Array.Empty<int>();
        private int[] _feat0Shape = Array.Empty<int>();
        private int[] _feat1Shape = Array.Empty<int>();

        private int _inputSize = 1024;
        private float _scale;
        private int _padH;
        private int _padW;

        public bool IsLoaded => _encoderSession != null && _decoderSession != null;

        public void LoadModels(string encoderPath, string decoderPath, bool useGpu = true)
        {
            SessionOptions? options = null;
            bool usingGpu = false;

            // Try GPU first if requested
            if (useGpu)
            {
                try
                {
                    options = new SessionOptions();
                    options.AppendExecutionProvider_DML();

                    // Test with a simple session creation
                    _encoderSession = new InferenceSession(encoderPath, options);
                    _decoderSession = new InferenceSession(decoderPath, options);
                    usingGpu = true;
                    Console.WriteLine("[SAM2] GPU (DirectML) initialized successfully.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SAM2] DirectML failed: {ex.Message}");
                    Console.WriteLine("[SAM2] Falling back to CPU...");

                    // Dispose any partially created sessions
                    _encoderSession?.Dispose();
                    _decoderSession?.Dispose();
                    _encoderSession = null;
                    _decoderSession = null;
                    options?.Dispose();
                    options = null;
                }
            }

            // CPU fallback
            if (!usingGpu)
            {
                try
                {
                    options = new SessionOptions();
                    // CPU only - no execution provider appended
                    _encoderSession = new InferenceSession(encoderPath, options);
                    _decoderSession = new InferenceSession(decoderPath, options);
                    Console.WriteLine("[SAM2] CPU mode initialized successfully.");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to load models (CPU fallback also failed): {ex.Message}", ex);
                }
            }

            Console.WriteLine("[SAM2] Models loaded successfully.");
            LogModelMetadata();
        }

        private void LogModelMetadata()
        {
            if (_encoderSession != null)
            {
                Console.WriteLine("--- Encoder Inputs ---");
                foreach (var input in _encoderSession.InputMetadata) Console.WriteLine($"Name: {input.Key}, Shape: {string.Join(",", input.Value.Dimensions)}");
                Console.WriteLine("--- Encoder Outputs ---");
                foreach (var output in _encoderSession.OutputMetadata) Console.WriteLine($"Name: {output.Key}, Shape: {string.Join(",", output.Value.Dimensions)}");
            }

            if (_decoderSession != null)
            {
                Console.WriteLine("--- Decoder Inputs ---");
                foreach (var input in _decoderSession.InputMetadata) Console.WriteLine($"Name: {input.Key}, Shape: {string.Join(",", input.Value.Dimensions)}");
            }
        }

        public void EncodeImage(string imagePath)
        {
            Console.WriteLine($"[SAM2] EncodeImage called with path: {imagePath}");
            using var mat = Cv2.ImRead(imagePath);
            if (mat.Empty())
            {
                Console.WriteLine("[SAM2] Failed to read image!");
                return;
            }
            EncodeImage(mat);
        }

        public void EncodeImage(Mat image)
        {
            if (_encoderSession == null)
            {
                Console.WriteLine("[SAM2] EncodeImage: Encoder not loaded!");
                throw new InvalidOperationException("Model not loaded");
            }

            try
            {
                Console.WriteLine($"[SAM2] Encoding image {image.Width}x{image.Height}...");

                // 1. Resize Longest Side to 1024
                int h = image.Height;
                int w = image.Width;
                _scale = _inputSize * 1.0f / Math.Max(h, w);
                int newH = (int)(h * _scale + 0.5f);
                int newW = (int)(w * _scale + 0.5f);

                using var resized = new Mat();
                Cv2.Resize(image, resized, new OpenCvSharp.Size(newW, newH));

                // 2. Normalize
                // Mean: [123.675, 116.28, 103.53], Std: [58.395, 57.12, 57.375]
                // Note: OpenCV reads as BGR, but SAM expects RGB.
                using var rgb = new Mat();
                Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

                // Convert to float and normalize
                using var floatMat = new Mat();
                rgb.ConvertTo(floatMat, MatType.CV_32FC3);

                // (pixel - mean) / std
                var channels = Cv2.Split(floatMat);
                channels[0] = (channels[0] - 123.675f) / 58.395f;
                channels[1] = (channels[1] - 116.28f) / 57.12f;
                channels[2] = (channels[2] - 103.53f) / 57.375f;
                Cv2.Merge(channels, floatMat);
                foreach (var c in channels) c.Dispose();

                // 3. Pad to 1024x1024 (Bottom-Right padding)
                _padH = _inputSize - newH;
                _padW = _inputSize - newW;
                using var padded = new Mat();
                Cv2.CopyMakeBorder(floatMat, padded, 0, _padH, 0, _padW, BorderTypes.Constant, new Scalar(0, 0, 0));

                // 4. Create Tensor [1, 3, 1024, 1024]
                var tensor = new DenseTensor<float>(new[] { 1, 3, _inputSize, _inputSize });

                // Fill tensor (CHW layout)
                var indexer = padded.GetGenericIndexer<Vec3f>();
                for (int y = 0; y < _inputSize; y++)
                {
                    for (int x = 0; x < _inputSize; x++)
                    {
                        Vec3f pixel = indexer[y, x];
                        tensor[0, 0, y, x] = pixel.Item0;
                        tensor[0, 1, y, x] = pixel.Item1;
                        tensor[0, 2, y, x] = pixel.Item2;
                    }
                }

                Console.WriteLine("[SAM2] Tensor prepared, running encoder...");

                // 5. Run Encoder
                var inputName = _encoderSession.InputMetadata.Keys.First();
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputName, tensor)
                };

                using var results = _encoderSession.Run(inputs);

                Console.WriteLine($"[SAM2] Encoder returned {results.Count} outputs:");
                foreach (var r in results)
                {
                    Console.WriteLine($"  - {r.Name}: {string.Join(",", r.AsTensor<float>().Dimensions.ToArray())}");
                }

                // 6. Cache Outputs - match by name
                var resultList = results.ToList();

                // Find embedding by name containing "embed"
                var embNode = resultList.FirstOrDefault(r => r.Name.Contains("embed"))
                           ?? resultList.OrderByDescending(r => r.AsTensor<float>().Length).First();

                // Find high res features by name
                var feat0Node = resultList.FirstOrDefault(r => r.Name.Contains("feats_0") || r.Name.Contains("feat_0"));
                var feat1Node = resultList.FirstOrDefault(r => r.Name.Contains("feats_1") || r.Name.Contains("feat_1"));

                _imageEmbeddings = embNode.AsTensor<float>().ToArray();
                _embeddingShape = embNode.AsTensor<float>().Dimensions.ToArray();
                Console.WriteLine($"[SAM2] Embeddings ({embNode.Name}): {string.Join(",", _embeddingShape)}");

                if (feat0Node != null && feat1Node != null)
                {
                    _highResFeats0 = feat0Node.AsTensor<float>().ToArray();
                    _feat0Shape = feat0Node.AsTensor<float>().Dimensions.ToArray();

                    _highResFeats1 = feat1Node.AsTensor<float>().ToArray();
                    _feat1Shape = feat1Node.AsTensor<float>().Dimensions.ToArray();

                    Console.WriteLine($"[SAM2] Feat0 ({feat0Node.Name}): {string.Join(",", _feat0Shape)}");
                    Console.WriteLine($"[SAM2] Feat1 ({feat1Node.Name}): {string.Join(",", _feat1Shape)}");
                }
                else
                {
                    // Fallback: use remaining outputs by size (descending = larger first for feat0)
                    var featNodes = resultList.Where(r => !r.Name.Contains("embed"))
                                              .OrderByDescending(r => r.AsTensor<float>().Length).ToList();

                    if (featNodes.Count >= 2)
                    {
                        _highResFeats0 = featNodes[0].AsTensor<float>().ToArray();
                        _feat0Shape = featNodes[0].AsTensor<float>().Dimensions.ToArray();
                        _highResFeats1 = featNodes[1].AsTensor<float>().ToArray();
                        _feat1Shape = featNodes[1].AsTensor<float>().Dimensions.ToArray();
                        Console.WriteLine($"[SAM2] Feat0 (fallback {featNodes[0].Name}): {string.Join(",", _feat0Shape)}");
                        Console.WriteLine($"[SAM2] Feat1 (fallback {featNodes[1].Name}): {string.Join(",", _feat1Shape)}");
                    }
                    else
                    {
                        Console.WriteLine("[SAM2] Warning: Could not find high-res features!");
                        _highResFeats0 = new float[1];
                        _feat0Shape = new[] { 1 };
                        _highResFeats1 = new float[1];
                        _feat1Shape = new[] { 1 };
                    }
                }

                Console.WriteLine("[SAM2] Image encoding complete!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SAM2] EncodeImage error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                _imageEmbeddings = null; // Ensure it's null on failure
                throw;
            }
        }

        public float[]? Predict(List<(Point point, bool isPositive)> prompts)
        {
            if (_decoderSession == null || _imageEmbeddings == null)
            {
                Console.WriteLine("[SAM2] Predict: Decoder not ready or image not encoded");
                return null;
            }

            try
            {
                // 1. Prepare Point Coords and Labels
                int numPoints = prompts.Count;
                var pointCoords = new DenseTensor<float>(new[] { 1, numPoints, 2 });
                var pointLabels = new DenseTensor<float>(new[] { 1, numPoints });

                for (int i = 0; i < numPoints; i++)
                {
                    // Map input coords to resized/padded image coords
                    float x = prompts[i].point.X * _scale;
                    float y = prompts[i].point.Y * _scale;

                    pointCoords[0, i, 0] = x;
                    pointCoords[0, i, 1] = y;

                    // Labels: 1 = Foreground, 0 = Background
                    pointLabels[0, i] = prompts[i].isPositive ? 1.0f : 0.0f;
                }

                // 2. Prepare Inputs - dynamically match input names
                var decoderInputs = _decoderSession.InputMetadata;
                var inputs = new List<NamedOnnxValue>();

                Console.WriteLine($"[SAM2] Decoder expects {decoderInputs.Count} inputs:");
                foreach (var input in decoderInputs)
                {
                    Console.WriteLine($"  - {input.Key}: {string.Join(",", input.Value.Dimensions)}");
                }

                // Match inputs by name patterns
                foreach (var inputMeta in decoderInputs)
                {
                    var name = inputMeta.Key;
                    var dims = inputMeta.Value.Dimensions;

                    if (name.Contains("embed"))
                    {
                        inputs.Add(NamedOnnxValue.CreateFromTensor(name,
                            new DenseTensor<float>(new Memory<float>(_imageEmbeddings!), new ReadOnlySpan<int>(_embeddingShape))));
                    }
                    else if (name.Contains("feat") && name.Contains("0"))
                    {
                        inputs.Add(NamedOnnxValue.CreateFromTensor(name,
                            new DenseTensor<float>(new Memory<float>(_highResFeats0!), new ReadOnlySpan<int>(_feat0Shape))));
                    }
                    else if (name.Contains("feat") && name.Contains("1"))
                    {
                        inputs.Add(NamedOnnxValue.CreateFromTensor(name,
                            new DenseTensor<float>(new Memory<float>(_highResFeats1!), new ReadOnlySpan<int>(_feat1Shape))));
                    }
                    else if (name.Contains("coord"))
                    {
                        inputs.Add(NamedOnnxValue.CreateFromTensor(name, pointCoords));
                    }
                    else if (name.Contains("label"))
                    {
                        inputs.Add(NamedOnnxValue.CreateFromTensor(name, pointLabels));
                    }
                    else if (name == "has_mask_input" || (name.Contains("has_mask") && !name.Contains("mask_input")))
                    {
                        // has_mask_input expects shape [1] - a single scalar
                        inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(new float[] { 0.0f }, new[] { 1 })));
                    }
                    else if (name.Contains("mask_input"))
                    {
                        // mask_input expects shape [1, 1, 256, 256]
                        inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(new[] { 1, 1, 256, 256 })));
                    }
                    else if (name.Contains("orig") || name.Contains("size"))
                    {
                        inputs.Add(NamedOnnxValue.CreateFromTensor(name,
                            new DenseTensor<float>(new float[] { (float)_inputSize, (float)_inputSize }, new[] { 2 })));
                    }
                    else
                    {
                        Console.WriteLine($"[SAM2] Unknown decoder input: {name}, creating zeros with shape {string.Join(",", dims)}");
                        // Create zero tensor with expected shape
                        var shape = dims.Select(d => d > 0 ? d : 1).ToArray();
                        inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(shape)));
                    }
                }

                Console.WriteLine($"[SAM2] Running decoder with {inputs.Count} inputs");

                // 3. Run Decoder
                using var results = _decoderSession.Run(inputs);

                Console.WriteLine($"[SAM2] Decoder returned {results.Count} outputs:");
                foreach (var r in results) Console.WriteLine($"  - {r.Name}");

                // 4. Process Output - find masks and iou by pattern
                var masksNode = results.FirstOrDefault(r => r.Name.Contains("mask") && !r.Name.Contains("iou"));
                var iouNode = results.FirstOrDefault(r => r.Name.Contains("iou") || r.Name.Contains("score"));

                if (masksNode == null)
                {
                    Console.WriteLine("[SAM2] No mask output found!");
                    return new float[] { 0, 0, 0, 0 };
                }

                var masks = masksNode.AsTensor<float>();
                Console.WriteLine($"[SAM2] Mask shape: {string.Join(",", masks.Dimensions.ToArray())}");

                // Find best mask
                int bestMaskIndex = 0;
                if (iouNode != null)
                {
                    var iou = iouNode.AsTensor<float>();
                    float maxIoU = -1;
                    int numMasks = iou.Dimensions.Length > 1 ? iou.Dimensions[1] : 1;

                    for (int i = 0; i < numMasks; i++)
                    {
                        float score = iou.Dimensions.Length > 1 ? iou[0, i] : iou[i];
                        if (score > maxIoU)
                        {
                            maxIoU = score;
                            bestMaskIndex = i;
                        }
                    }
                    Console.WriteLine($"[SAM2] Best mask index: {bestMaskIndex}, IoU: {maxIoU}");
                }

                // Get mask dimensions
                int maskH = masks.Dimensions[^2];
                int maskW = masks.Dimensions[^1];

                // Extract Bounding Box from mask
                float minX = maskW, minY = maskH, maxX = 0, maxY = 0;
                bool found = false;

                for (int y = 0; y < maskH; y++)
                {
                    for (int x = 0; x < maskW; x++)
                    {
                        float val = masks.Dimensions.Length == 4 ? masks[0, bestMaskIndex, y, x] : masks[0, y, x];
                        if (val > 0.0f)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                            found = true;
                        }
                    }
                }

                if (!found)
                {
                    Console.WriteLine("[SAM2] No positive mask pixels found");
                    return new float[] { 0, 0, 0, 0 };
                }

                // Scale back to original coordinates
                float scaleToInput = (float)_inputSize / maskW;
                minX *= scaleToInput / _scale;
                maxX *= scaleToInput / _scale;
                minY *= scaleToInput / _scale;
                maxY *= scaleToInput / _scale;

                Console.WriteLine($"[SAM2] BBox: ({minX}, {minY}, {maxX - minX}, {maxY - minY})");
                return new float[] { minX, minY, maxX - minX, maxY - minY };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SAM2] Predict error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return null;
            }
        }
        public void Dispose()
        {
            _encoderSession?.Dispose();
            _decoderSession?.Dispose();
        }
    }
}
