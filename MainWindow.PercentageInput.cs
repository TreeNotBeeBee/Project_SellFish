using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowPilot;

public sealed partial class MainWindow
{
    // 每个百分比输入框独立延迟提交，使用透明度快捷键时也能保留其他框的待提交输入。
    private sealed class PercentageInput
    {
        private readonly MainWindow _owner;
        private readonly TextBox _field;
        private readonly string _label;
        private readonly Func<int> _current;
        private readonly Action<int> _apply;
        private readonly DispatcherQueueTimer _timer;
        private bool _writing;

        public PercentageInput(MainWindow owner, TextBox field, string label, Func<int> current, Action<int> apply)
        {
            _owner = owner;
            _field = field;
            _label = label;
            _current = current;
            _apply = apply;
            _timer = owner.DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(320);
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => owner.Run(() => Commit(finalize: false));
            field.TextChanged += (_, _) =>
            {
                if (_writing || !CanEdit) return;
                _timer.Stop();
                _timer.Start();
            };
            field.LostFocus += (_, _) => owner.Run(() => Commit(finalize: true));
            field.KeyDown += (_, args) =>
            {
                if (args.Key == VirtualKey.Enter)
                {
                    args.Handled = true;
                    owner.Run(() => Commit(finalize: true));
                }
                else if (args.Key == VirtualKey.Escape)
                {
                    args.Handled = true;
                    ShowValue();
                }
            };
        }

        private bool CanEdit => _owner._ready && !_owner._updating && _field.IsEnabled && _owner._windows.Target != null;

        public int PendingOrCurrentValue => _timer.IsRunning && TryRead(out int pending) ? pending : _current();

        public void Stop() => _timer.Stop();

        public void ShowValue()
        {
            _timer.Stop();
            _writing = true;
            try { _field.Text = _current().ToString(CultureInfo.InvariantCulture); }
            finally { _writing = false; }
        }

        private bool TryRead(out int value)
        {
            string raw = _field.Text.Trim().TrimEnd('%', '％').Trim();
            return int.TryParse(raw, NumberStyles.None, CultureInfo.CurrentCulture, out value) && value is >= 0 and <= 100;
        }

        private void Commit(bool finalize)
        {
            _timer.Stop();
            if (!CanEdit) return;
            if (TryRead(out int value))
            {
                if (value != _current()) _apply(value);
            }
            else if (finalize)
            {
                _owner.Status(_label + "可输入 0 到 100 的整数。");
            }
            if (finalize) ShowValue();
        }
    }
}
