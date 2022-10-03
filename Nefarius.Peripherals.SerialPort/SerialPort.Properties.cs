using Nefarius.Peripherals.SerialPort.Win32PInvoke;

namespace Nefarius.Peripherals.SerialPort;

public partial class SerialPort
{
    /// <summary>
    ///     If true, the port will automatically re-open on next send if it was previously closed due
    ///     to an error (default: false)
    /// </summary>
    public bool AutoReopen { get; set; }

    /// <summary>
    ///     Baud Rate (default: 115200)
    /// </summary>
    /// <remarks>Unsupported rates will throw "Bad settings".</remarks>
    public int BaudRate { get; set; } = 115200;

    /// <summary>
    ///     If true, subsequent Send commands wait for completion of earlier ones enabling the results
    ///     to be checked. If false, errors, including timeouts, may not be detected, but performance
    ///     may be better.
    /// </summary>
    public bool CheckAllSends { get; set; } = true;

    /// <summary>
    ///     Number of databits 1..8 (default: 8) unsupported values will throw "Bad settings"
    /// </summary>
    public int DataBits { get; set; } = 8;

    /// <summary>
    ///     The parity checking scheme (default: none)
    /// </summary>
    public Parity Parity { get; set; } = Parity.None;

    /// <summary>
    ///     If true, Xon and Xoff characters are sent to control the data flow from the remote station (default: false)
    /// </summary>
    public bool RxFlowX { get; set; }

    /// <summary>
    ///     If true, received characters are ignored unless DSR is asserted by the remote station (default: false)
    /// </summary>
    public bool RxGateDsr { get; set; }

    /// <summary>
    ///     The number of free bytes in the reception queue at which flow is disabled (default: 2048)
    /// </summary>
    public int RxHighWater { get; set; } = 2048;

    /// <summary>
    ///     The number of bytes in the reception queue at which flow is re-enabled (default: 512)
    /// </summary>
    public int RxLowWater { get; set; } = 512;

    /// <summary>
    ///     Requested size for receive queue (default: 0 = use operating system default)
    /// </summary>
    public int RxQueue { get; set; }

    /// <summary>
    ///     Constant.  Max time for Send in ms = (Multiplier * Characters) + Constant (default: 0)
    /// </summary>
    public int SendTimeoutConstant { get; set; }

    /// <summary>
    ///     Multiplier. Max time for Send in ms = (Multiplier * Characters) + Constant
    ///     (default: 0 = No timeout)
    /// </summary>
    public int SendTimeoutMultiplier { get; set; }

    /// <summary>
    ///     Number of stop bits (default: one)
    /// </summary>
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>
    ///     If true, transmission is halted unless CTS is asserted by the remote station (default: false)
    /// </summary>
    public bool TxFlowCts { get; set; }

    /// <summary>
    ///     If true, transmission is halted unless DSR is asserted by the remote station (default: false)
    /// </summary>
    public bool TxFlowDsr { get; set; }

    /// <summary>
    ///     If true, transmission is halted when Xoff is received and restarted when Xon is received (default: false)
    /// </summary>
    public bool TxFlowX { get; set; }

    /// <summary>
    ///     Requested size for transmit queue (default: 0 = use operating system default)
    /// </summary>
    public int TxQueue { get; set; }

    /// <summary>
    ///     If false, transmission is suspended when this station has sent Xoff to the remote station (default: true)
    ///     Set false if the remote station treats any character as an Xon.
    /// </summary>
    public bool TxWhenRxXoff { get; set; } = true;

    /// <summary>
    ///     Specidies the use to which the DTR output is put (default: none)
    /// </summary>
    public HsOutput UseDtr { get; set; } = HsOutput.None;

    /// <summary>
    ///     Specifies the use to which the RTS output is put (default: none)
    /// </summary>
    public HsOutput UseRts { get; set; } = HsOutput.None;

    /// <summary>
    ///     The character used to signal Xoff for X flow control (default: DC3)
    /// </summary>
    public ASCII XoffChar { get; set; } = ASCII.DC3;

    /// <summary>
    ///     The character used to signal Xon for X flow control (default: DC1)
    /// </summary>
    public ASCII XonChar { get; set; } = ASCII.DC1;

    /// <summary>
    ///     True if online.
    /// </summary>
    public bool Online => _online && CheckOnline();

    /// <summary>
    ///     True if the RTS pin is controllable via the RTS property
    /// </summary>
    protected bool RtSavailable => _stateRts < 2;

    /// <summary>
    ///     Set the state of the RTS modem control output
    /// </summary>
    protected bool Rts
    {
        set
        {
            if (_stateRts > 1) return;
            CheckOnline();
            if (value)
            {
                if (Win32Com.EscapeCommFunction(_hPort.DangerousGetHandle(), Win32Com.SETRTS))
                    _stateRts = 1;
                else
                    ThrowException("Unexpected Failure");
            }
            else
            {
                if (Win32Com.EscapeCommFunction(_hPort.DangerousGetHandle(), Win32Com.CLRRTS))
                    _stateRts = 1;
                else
                    ThrowException("Unexpected Failure");
            }
        }
        get => _stateRts == 1;
    }

    /// <summary>
    ///     True if the DTR pin is controllable via the DTR property
    /// </summary>
    protected bool DtrAvailable => _stateDtr < 2;

    /// <summary>
    ///     The state of the DTR modem control output
    /// </summary>
    protected bool Dtr
    {
        set
        {
            if (_stateDtr > 1) return;
            CheckOnline();
            if (value)
            {
                if (Win32Com.EscapeCommFunction(_hPort.DangerousGetHandle(), Win32Com.SETDTR))
                    _stateDtr = 1;
                else
                    ThrowException("Unexpected Failure");
            }
            else
            {
                if (Win32Com.EscapeCommFunction(_hPort.DangerousGetHandle(), Win32Com.CLRDTR))
                    _stateDtr = 0;
                else
                    ThrowException("Unexpected Failure");
            }
        }
        get => _stateDtr == 1;
    }

    /// <summary>
    ///     Assert or remove a break condition from the transmission line
    /// </summary>
    protected bool Break
    {
        set
        {
            if (_stateBrk > 1) return;
            CheckOnline();
            if (value)
            {
                if (Win32Com.EscapeCommFunction(_hPort.DangerousGetHandle(), Win32Com.SETBREAK))
                    _stateBrk = 0;
                else
                    ThrowException("Unexpected Failure");
            }
            else
            {
                if (Win32Com.EscapeCommFunction(_hPort.DangerousGetHandle(), Win32Com.CLRBREAK))
                    _stateBrk = 0;
                else
                    ThrowException("Unexpected Failure");
            }
        }
        get => _stateBrk == 1;
    }
    
    /// <summary>
    ///     Port Name
    /// </summary>
    public string PortName { get; set; }

    public Handshake Handshake
    {
        get => _handShake;
        set
        {
            _handShake = value;
            switch (_handShake)
            {
                case Handshake.None:
                    TxFlowCts = false;
                    TxFlowDsr = false;
                    TxFlowX = false;
                    RxFlowX = false;
                    UseRts = HsOutput.Online;
                    UseDtr = HsOutput.Online;
                    TxWhenRxXoff = true;
                    RxGateDsr = false;
                    break;
                case Handshake.XonXoff:
                    TxFlowCts = false;
                    TxFlowDsr = false;
                    TxFlowX = true;
                    RxFlowX = true;
                    UseRts = HsOutput.Online;
                    UseDtr = HsOutput.Online;
                    TxWhenRxXoff = true;
                    RxGateDsr = false;
                    XonChar = ASCII.DC1;
                    XoffChar = ASCII.DC3;
                    break;
                case Handshake.CtsRts:
                    TxFlowCts = true;
                    TxFlowDsr = false;
                    TxFlowX = false;
                    RxFlowX = false;
                    UseRts = HsOutput.Handshake;
                    UseDtr = HsOutput.Online;
                    TxWhenRxXoff = true;
                    RxGateDsr = false;
                    break;
                case Handshake.DsrDtr:
                    TxFlowCts = false;
                    TxFlowDsr = true;
                    TxFlowX = false;
                    RxFlowX = false;
                    UseRts = HsOutput.Online;
                    UseDtr = HsOutput.Handshake;
                    TxWhenRxXoff = true;
                    RxGateDsr = false;
                    break;
            }
        }
    }
}