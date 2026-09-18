using System.Device.Gpio;

namespace Rfm95w.Tests
{
    /// <summary>
    /// Minimal GPIO driver: remembers pin modes and values, logs writes, and lets a test fire pin-change events.
    /// </summary>
    public sealed class FakeGpioDriver : GpioDriver
    {
        private readonly Dictionary<int, PinMode> _modes = new();
        private readonly Dictionary<int, PinValue> _values = new();
        private readonly Dictionary<int, List<(PinEventTypes Types, PinChangeEventHandler Handler)>> _callbacks = new();

        /// <summary>(pin, value) in write order.</summary>
        public List<(int Pin, PinValue Value)> WriteLog { get; } = new();

        public IReadOnlyCollection<int> OpenPins => _modes.Keys;

        public int CallbackCount(int pin) => _callbacks.TryGetValue(pin, out var list) ? list.Count : 0;

        /// <summary>Invokes every callback registered for the pin with a matching event type, on the calling thread.</summary>
        public void Fire(int pin, PinEventTypes type)
        {
            if (!_callbacks.TryGetValue(pin, out var list))
            {
                return;
            }

            foreach ((PinEventTypes types, PinChangeEventHandler handler) in list.ToArray())
            {
                if (types.HasFlag(type))
                {
                    handler(this, new PinValueChangedEventArgs(type, pin));
                }
            }
        }

        protected override int PinCount => 28;

        protected override void OpenPin(int pinNumber)
        {
            _modes[pinNumber] = PinMode.Input;
            _values.TryAdd(pinNumber, PinValue.Low);
        }

        protected override void ClosePin(int pinNumber)
        {
            _modes.Remove(pinNumber);
            _callbacks.Remove(pinNumber);
        }

        protected override void SetPinMode(int pinNumber, PinMode mode) => _modes[pinNumber] = mode;

        protected override PinMode GetPinMode(int pinNumber) => _modes[pinNumber];

        protected override bool IsPinModeSupported(int pinNumber, PinMode mode) => true;

        protected override PinValue Read(int pinNumber) => _values.TryGetValue(pinNumber, out PinValue v) ? v : PinValue.Low;

        protected override void Write(int pinNumber, PinValue value)
        {
            _values[pinNumber] = value;
            WriteLog.Add((pinNumber, value));
        }

        protected override WaitForEventResult WaitForEvent(int pinNumber, PinEventTypes eventTypes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override void AddCallbackForPinValueChangedEvent(int pinNumber, PinEventTypes eventTypes, PinChangeEventHandler callback)
        {
            if (!_callbacks.TryGetValue(pinNumber, out var list))
            {
                list = new List<(PinEventTypes, PinChangeEventHandler)>();
                _callbacks[pinNumber] = list;
            }

            list.Add((eventTypes, callback));
        }

        protected override void RemoveCallbackForPinValueChangedEvent(int pinNumber, PinChangeEventHandler callback)
        {
            if (_callbacks.TryGetValue(pinNumber, out var list))
            {
                list.RemoveAll(x => x.Handler == callback);
            }
        }
    }
}
