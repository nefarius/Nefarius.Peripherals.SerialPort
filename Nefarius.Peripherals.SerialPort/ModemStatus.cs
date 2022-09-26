﻿using Windows.Win32.Devices.Communication;

namespace Nefarius.Peripherals.SerialPort;

/// <summary>
///     Represents the status of the modem control input signals.
/// </summary>
public readonly struct ModemStatus
{
    private readonly uint _status;

    internal ModemStatus(uint val)
    {
        _status = val;
    }

    /// <summary>
    ///     Condition of the Clear To Send signal.
    /// </summary>
    public bool Cts => (_status & (uint)MODEM_STATUS_FLAGS.MS_CTS_ON) != 0;

    /// <summary>
    ///     Condition of the Data Set Ready signal.
    /// </summary>
    public bool Dsr => (_status & (uint)MODEM_STATUS_FLAGS.MS_DSR_ON) != 0;

    /// <summary>
    ///     Condition of the Receive Line Status Detection signal.
    /// </summary>
    public bool Rlsd => (_status & (uint)MODEM_STATUS_FLAGS.MS_RLSD_ON) != 0;

    /// <summary>
    ///     Condition of the Ring Detection signal.
    /// </summary>
    public bool Ring => (_status & (uint)MODEM_STATUS_FLAGS.MS_RING_ON) != 0;
}