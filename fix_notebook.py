import json
from pathlib import Path

notebook_path = Path(__file__).resolve().parent / 'YOLO_TRAIN_KaggleDataset.ipynb'

with notebook_path.open('r', encoding='utf-8') as f:
    nb = json.load(f)

# Fix cell that uses results.save_dir (the export/display cell)
for cell in nb['cells']:
    if cell['cell_type'] == 'code':
        source_text = ''.join(cell['source'])
        if 'results.save_dir' in source_text:
            new_source = []
            for line in cell['source']:
                if "save_dir = str(results.save_dir)" in line:
                    # Replace with a DDP-safe fallback
                    new_source.append("# DDP 模式下 results 可能为 None，手动构建路径\n")
                    new_source.append("if results is not None and hasattr(results, 'save_dir'):\n")
                    new_source.append("    save_dir = str(results.save_dir)\n")
                    new_source.append("else:\n")
                    new_source.append("    save_dir = os.path.join(RUNS_PROJECT, run_name)\n")
                    new_source.append("print(f'结果目录: {save_dir}')\n")
                elif "output_name = f'detector_{model_family}{model_size}.onnx'" in line:
                    # Also fix: use weight_prefix instead of model_family
                    new_source.append("    output_name = f'detector_{weight_prefix}{model_size}.onnx'\n")
                else:
                    new_source.append(line)
            cell['source'] = new_source
            print("✅ Fixed results.save_dir cell")

with notebook_path.open('w', encoding='utf-8') as f:
    json.dump(nb, f, indent=1, ensure_ascii=False)

print("✅ YOLO_TRAIN_KaggleDataset.ipynb patched successfully")
