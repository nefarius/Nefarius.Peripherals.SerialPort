using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Devices.Communication;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Microsoft.Win32.SafeHandles;
using Nefarius.Peripherals.SerialPort.Win32PInvoke;

namespace Nefarius.Peripherals.SerialPort;

/// <summary>
///     Wrapper class around a serial (COM, RS-232) port.
/// </summary>
public partial class SerialPort : IDisposable
{
    private readonly ManualResetEvent _writeEvent = new(false);
    private bool _auto;
    private bool _checkSends = true;

    private Handshake _handShake;
    private SafeFileHandle _hPort;
    private bool _online;
    private NativeOverlapped _ptrUwo;
    private Exception _rxException;
    private bool _rxExceptionReported;
    private Thread _rxThread;
    private int _stateBrk = 2;
    private int _stateDtr = 2;
    private int _stateRts = 2;
    private int _writeCount;

    /// <summary>
    ///     Class constructor
    /// </summary>
    public SerialPort(string portName)
    {
        PortName = portName;
    }

    /// <inheritdoc />
    /// <summary>
    ///     Class constructor
    /// </summary>
    public SerialPort(string portName, int baudRate) : this(portName)
    {
        BaudRate = baudRate;
    }

    /// <inheritdoc />
    /// <summary>
    ///     For IDisposable
    /// </summary>
    public void Dispose()
    {
        Close();
    }

    /// <summary>
    ///     Opens the com port and configures it with the required settings
    /// </summary>
    /// <returns>false if the port could not be opened</returns>
    public bool Open()
    {
        var portDcb = new DCB();
        var commTimeouts = new COMMTIMEOUTS();

        if (_online) return false;

        _hPort = PInvoke.CreateFile(PortName,
            FILE_ACCESS_FLAGS.FILE_GENERIC_READ | FILE_ACCESS_FLAGS.FILE_GENERIC_WRITE, 0,
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED, null);

        if (_hPort.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == (int)WIN32_ERROR.ERROR_ACCESS_DENIED) return false;
            throw new CommPortException("Port Open Failure");
        }

        _online = true;

        commTimeouts.ReadIntervalTimeout = 0;
        commTimeouts.ReadTotalTimeoutConstant = 0;
        commTimeouts.ReadTotalTimeoutMultiplier = 0;
        commTimeouts.WriteTotalTimeoutConstant = (uint)SendTimeoutConstant;
        commTimeouts.WriteTotalTimeoutMultiplier = (uint)SendTimeoutMultiplier;
        portDcb.Init(Parity is Parity.Odd or Parity.Even, TxFlowCts, TxFlowDsr,
            (int)UseDtr, RxGateDsr, !TxWhenRxXoff, TxFlowX, RxFlowX, (int)UseRts);
        portDcb.BaudRate = (uint)BaudRate;
        portDcb.ByteSize = (byte)DataBits;
        portDcb.Parity = (DCB_PARITY)Parity;
        portDcb.StopBits = (DCB_STOP_BITS)StopBits;
        portDcb.XoffChar = (CHAR)(byte)XoffChar;
        portDcb.XonChar = (CHAR)(byte)XonChar;
        portDcb.XoffLim = (ushort)RxHighWater;
        portDcb.XonLim = (ushort)RxLowWater;

        if (RxQueue != 0 || TxQueue != 0)
            if (!PInvoke.SetupComm(_hPort, (uint)RxQueue, (uint)TxQueue))
                ThrowException("Bad queue settings");

        if (!PInvoke.SetCommState(_hPort, portDcb))
            ThrowException("Bad com settings");

        if (!PInvoke.SetCommTimeouts(_hPort, commTimeouts))
            ThrowException("Bad timeout settings");

        _stateBrk = 0;
        switch (UseDtr)
        {
            case HsOutput.None:
                _stateDtr = 0;
                break;
            case HsOutput.Online:
                _stateDtr = 1;
                break;
        }

        switch (UseRts)
        {
            case HsOutput.None:
                _stateRts = 0;
                break;
            case HsOutput.Online:
                _stateRts = 1;
                break;
        }

        _checkSends = CheckAllSends;
        _ptrUwo.EventHandle = _checkSends ? _writeEvent.SafeWaitHandle.DangerousGetHandle() : IntPtr.Zero;
        _writeCount = 0;

        _rxException = null;
        _rxExceptionReported = false;

        // TODO: utilize Task Parallel Library here
        _rxThread = new Thread(ReceiveThread)
        {
            Name = "CommBaseRx",
            Priority = ThreadPriority.AboveNormal,
            IsBackground = true
        };

        _rxThread.Start();
        Thread.Sleep(1); //Give rx thread time to start. By documentation, 0 should work, but it does not!

        _auto = false;
        if (AfterOpen())
        {
            _auto = AutoReopen;
            return true;
        }

        Close();
        return false;
    }

    /// <summary>
    ///     Closes the com port.
    /// </summary>
    public void Close()
    {
        if (_online)
        {
            _auto = false;
            BeforeClose(false);
            InternalClose();
            _rxException = null;
        }
    }

    private void InternalClose()
    {
        Win32Com.CancelIo(_hPort.DangerousGetHandle());
        if (_rxThread != null)
        {
            _rxThread.Abort();
            _rxThread = null;
        }

        _hPort.Dispose();
        _stateRts = 2;
        _stateDtr = 2;
        _stateBrk = 2;
        _online = false;
    }

    /// <summary>
    ///     Destructor (just in case)
    /// </summary>
    ~SerialPort()
    {
        Close();
    }

    /// <summary>
    ///     Block until all bytes in the queue have been transmitted.
    /// </summary>
    public void Flush()
    {
        CheckOnline();
        CheckResult();
    }

    /// <summary>
    ///     Use this to throw exceptions in derived classes. Correctly handles threading issues
    ///     and closes the port if necessary.
    /// </summary>
    /// <param name="reason">Description of fault</param>
    protected void ThrowException(string reason)
    {
        if (Thread.CurrentThread == _rxThread) throw new CommPortException(reason);
        if (_online)
        {
            BeforeClose(true);
            InternalClose();
        }

        if (_rxException == null) throw new CommPortException(reason);
        throw new CommPortException(_rxException);
    }

    /// <summary>
    ///     Queues bytes for transmission.
    /// </summary>
    /// <param name="toSend">Array of bytes to be sent</param>
    public unsafe void Write(byte[] toSend)
    {
        uint sent;
        CheckOnline();
        CheckResult();
        _writeCount = toSend.GetLength(0);

        fixed (byte* ptr = toSend)
        fixed (NativeOverlapped* ptrOl = &_ptrUwo)
        {
            if (PInvoke.WriteFile(_hPort, ptr, (uint)_writeCount, &sent, ptrOl))
            {
                _writeCount -= (int)sent;
            }
            else
            {
                if (Marshal.GetLastWin32Error() != (int)WIN32_ERROR.ERROR_IO_PENDING)
                    ThrowException("Unexpected failure");
            }
        }
    }

    /// <summary>
    ///     Queues string for transmission.
    /// </summary>
    /// <param name="toSend">Array of bytes to be sent</param>
    public void Write(string toSend)
    {
        Write(new ASCIIEncoding().GetBytes(toSend));
    }

    /// <summary>
    ///     Queues a single byte for transmission.
    /// </summary>
    /// <param name="toSend">Byte to be sent</param>
    public void Write(byte toSend)
    {
        var b = new byte[1];
        b[0] = toSend;
        Write(b);
    }

    /// <summary>
    ///     Queues a single char for transmission.
    /// </summary>
    /// <param name="toSend">Byte to be sent</param>
    public void Write(char toSend)
    {
        Write(toSend.ToString());
    }

    /// <summary>
    ///     Queues string with a new line ("\r\n") for transmission.
    /// </summary>
    /// <param name="toSend">Array of bytes to be sent</param>
    public void WriteLine(string toSend)
    {
        Write(new ASCIIEncoding().GetBytes(toSend + Environment.NewLine));
    }

    private void CheckResult()
    {
        if (_writeCount <= 0) return;
        if (PInvoke.GetOverlappedResult(_hPort, _ptrUwo, out var sent, _checkSends))
        {
            _writeCount -= (int)sent;
            if (_writeCount != 0) ThrowException("Send Timeout");
        }
        else
        {
            if (Marshal.GetLastWin32Error() != (int)WIN32_ERROR.ERROR_IO_PENDING) ThrowException("Unexpected failure");
        }
    }

    /// <summary>
    ///     Sends a protocol byte immediately ahead of any queued bytes.
    /// </summary>
    /// <param name="tosend">Byte to send</param>
    /// <returns>False if an immediate byte is already scheduled and not yet sent</returns>
    public void SendImmediate(byte tosend)
    {
        CheckOnline();
        if (!Win32Com.TransmitCommChar(_hPort.DangerousGetHandle(), tosend)) ThrowException("Transmission failure");
    }

    /// <summary>
    ///     Gets the status of the modem control input signals.
    /// </summary>
    /// <returns>Modem status object</returns>
    protected ModemStatus GetModemStatus()
    {
        uint f;

        CheckOnline();
        if (!Win32Com.GetCommModemStatus(_hPort.DangerousGetHandle(), out f)) ThrowException("Unexpected failure");
        return new ModemStatus(f);
    }

    /// <summary>
    ///     Get the status of the queues
    /// </summary>
    /// <returns>Queue status object</returns>
    protected unsafe QueueStatus GetQueueStatus()
    {
        COMSTAT cs;
        var cp = new COMMPROP();
        CLEAR_COMM_ERROR_FLAGS er;

        CheckOnline();
        if (!PInvoke.ClearCommError(_hPort, &er, &cs))
            ThrowException("Unexpected failure");

        if (!PInvoke.GetCommProperties(_hPort, ref cp))
            ThrowException("Unexpected failure");

        return new QueueStatus(cs._bitfield, cs.cbInQue, cs.cbOutQue, cp.dwCurrentRxQueue, cp.dwCurrentTxQueue);
    }

    /// <summary>
    ///     Override this to provide processing after the port is opened (i.e. to configure remote
    ///     device or just check presence).
    /// </summary>
    /// <returns>false to close the port again</returns>
    protected virtual bool AfterOpen()
    {
        return true;
    }

    /// <summary>
    ///     Override this to provide processing prior to port closure.
    /// </summary>
    /// <param name="error">True if closing due to an error</param>
    protected virtual void BeforeClose(bool error)
    {
    }

    public event Action<byte> DataReceived;

    /// <summary>
    ///     Override this to process received bytes.
    /// </summary>
    /// <param name="ch">The byte that was received</param>
    protected void OnRxChar(byte ch)
    {
        DataReceived?.Invoke(ch);
    }

    /// <summary>
    ///     Override this to take action when transmission is complete (i.e. all bytes have actually
    ///     been sent, not just queued).
    /// </summary>
    protected virtual void OnTxDone()
    {
    }

    /// <summary>
    ///     Override this to take action when a break condition is detected on the input line.
    /// </summary>
    protected virtual void OnBreak()
    {
    }

    /// <summary>
    ///     Override this to take action when a ring condition is signaled by an attached modem.
    /// </summary>
    protected virtual void OnRing()
    {
    }

    /// <summary>
    ///     Override this to take action when one or more modem status inputs change state
    /// </summary>
    /// <param name="mask">The status inputs that have changed state</param>
    /// <param name="state">The state of the status inputs</param>
    protected virtual void OnStatusChange(ModemStatus mask, ModemStatus state)
    {
    }

    /// <summary>
    ///     Override this to take action when the reception thread closes due to an exception being thrown.
    /// </summary>
    /// <param name="e">The exception which was thrown</param>
    protected virtual void OnRxException(Exception e)
    {
    }

    private unsafe void ReceiveThread()
    {
        var buf = stackalloc byte[1];

        var sg = new AutoResetEvent(false);
        var ov = new NativeOverlapped
        {
            EventHandle = sg.SafeWaitHandle.DangerousGetHandle()
        };

        COMM_EVENT_MASK eventMask = 0;

        try
        {
            while (true)
            {
                if (!PInvoke.SetCommMask(_hPort,
                        COMM_EVENT_MASK.EV_RXCHAR | COMM_EVENT_MASK.EV_TXEMPTY | COMM_EVENT_MASK.EV_CTS |
                        COMM_EVENT_MASK.EV_DSR
                        | COMM_EVENT_MASK.EV_BREAK | COMM_EVENT_MASK.EV_RLSD | COMM_EVENT_MASK.EV_RING |
                        COMM_EVENT_MASK.EV_ERR))
                    throw new CommPortException("IO Error [001]");

                if (!PInvoke.WaitCommEvent(_hPort, ref eventMask, &ov))
                {
                    if (Marshal.GetLastWin32Error() == (int)WIN32_ERROR.ERROR_IO_PENDING)
                        sg.WaitOne();
                    else
                        throw new CommPortException("IO Error [002]");
                }

                if ((eventMask & COMM_EVENT_MASK.EV_ERR) != 0)
                {
                    CLEAR_COMM_ERROR_FLAGS errs;
                    if (PInvoke.ClearCommError(_hPort, &errs, null))
                    {
                        var s = new StringBuilder("UART Error: ", 40);
                        if (((uint)errs & Win32Com.CE_FRAME) != 0) s = s.Append("Framing,");
                        if (((uint)errs & Win32Com.CE_IOE) != 0) s = s.Append("IO,");
                        if (((uint)errs & Win32Com.CE_OVERRUN) != 0) s = s.Append("Overrun,");
                        if (((uint)errs & Win32Com.CE_RXOVER) != 0) s = s.Append("Receive Overflow,");
                        if (((uint)errs & Win32Com.CE_RXPARITY) != 0) s = s.Append("Parity,");
                        if (((uint)errs & Win32Com.CE_TXFULL) != 0) s = s.Append("Transmit Overflow,");
                        s.Length -= 1;
                        throw new CommPortException(s.ToString());
                    }

                    throw new CommPortException("IO Error [003]");
                }

                if ((eventMask & COMM_EVENT_MASK.EV_RXCHAR) != 0)
                {
                    uint gotbytes;
                    do
                    {
                        if (!PInvoke.ReadFile(_hPort, buf, 1, &gotbytes, &ov))
                        {
                            if (Marshal.GetLastWin32Error() == (int)WIN32_ERROR.ERROR_IO_PENDING)
                            {
                                Win32Com.CancelIo(_hPort.DangerousGetHandle());
                                gotbytes = 0;
                            }
                            else
                            {
                                throw new CommPortException("IO Error [004]");
                            }
                        }

                        if (gotbytes == 1) OnRxChar(buf[0]);
                    } while (gotbytes > 0);
                }

                if ((eventMask & COMM_EVENT_MASK.EV_TXEMPTY) != 0) OnTxDone();
                if ((eventMask & COMM_EVENT_MASK.EV_BREAK) != 0) OnBreak();

                uint i = 0;
                if ((eventMask & COMM_EVENT_MASK.EV_CTS) != 0) i |= Win32Com.MS_CTS_ON;
                if ((eventMask & COMM_EVENT_MASK.EV_DSR) != 0) i |= Win32Com.MS_DSR_ON;
                if ((eventMask & COMM_EVENT_MASK.EV_RLSD) != 0) i |= Win32Com.MS_RLSD_ON;
                if ((eventMask & COMM_EVENT_MASK.EV_RING) != 0) i |= Win32Com.MS_RING_ON;
                if (i != 0)
                {
                    uint f;
                    if (!Win32Com.GetCommModemStatus(_hPort.DangerousGetHandle(), out f))
                        throw new CommPortException("IO Error [005]");
                    OnStatusChange(new ModemStatus(i), new ModemStatus(f));
                }
            }
        }
        catch (Exception e)
        {
            if (!(e is ThreadAbortException))
            {
                _rxException = e;
                OnRxException(e);
            }
        }
    }

    private bool CheckOnline()
    {
        if (_rxException != null && !_rxExceptionReported)
        {
            _rxExceptionReported = true;
            ThrowException("rx");
        }

        if (_online)
        {
            uint f;
            if (Win32Com.GetHandleInformation(_hPort.DangerousGetHandle(), out f)) return true;
            ThrowException("Offline");
            return false;
        }

        if (_auto)
            if (Open())
                return true;
        ThrowException("Offline");
        return false;
    }
}