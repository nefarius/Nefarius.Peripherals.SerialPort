using System;
using Nefarius.Peripherals.SerialPort;

namespace PInvokeSerialPort.Sample;

internal class Program
{
    private static void Main(string[] args)
    {
        var serialPort = new SerialPort("COM7") { UseRts = HsOutput.Online };

        serialPort.DataReceived += x => Console.Write((char)x);

        serialPort.Open();

        serialPort.Write("START\r\n");

        Console.ReadKey();
    }
}