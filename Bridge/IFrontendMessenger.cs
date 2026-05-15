namespace Insight.Bridge
{
    public interface IFrontendMessenger
    {
        void Send(object data);
        void Log(string message, string type = "info");
        void Error(string message);
        void Complete(string message);
    }
}
