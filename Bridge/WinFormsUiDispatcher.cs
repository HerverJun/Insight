namespace Insight.Bridge
{
    public sealed class WinFormsUiDispatcher : IUiDispatcher
    {
        private readonly Control _control;

        public WinFormsUiDispatcher(Control control)
        {
            _control = control;
        }

        public bool InvokeRequired => !_control.IsDisposed && _control.InvokeRequired;

        public void Post(Action action)
        {
            if (_control.IsDisposed) return;

            if (_control.InvokeRequired)
            {
                _control.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }

        public void Invoke(Action action)
        {
            if (_control.IsDisposed) return;

            if (_control.InvokeRequired)
            {
                _control.Invoke(action);
            }
            else
            {
                action();
            }
        }

        public T Invoke<T>(Func<T> action)
        {
            if (_control.InvokeRequired)
            {
                return (T)_control.Invoke(action);
            }

            return action();
        }
    }
}
