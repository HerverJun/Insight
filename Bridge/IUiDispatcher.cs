namespace Insight.Bridge
{
    public interface IUiDispatcher
    {
        bool InvokeRequired { get; }
        void Post(Action action);
        void Invoke(Action action);
        T Invoke<T>(Func<T> action);
    }
}
