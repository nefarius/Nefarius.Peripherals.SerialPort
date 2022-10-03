using System;
using Nefarius.Peripherals.SerialPort;

namespace PInvokeSerialPort.Sample;

internal class Program
{
    private static void Main(string[] args)
    {
        var serialPort = new SerialPort("com7") { UseRts = HsOutput.Online };

        serialPort.DataReceived += x =>
        {
            Console.Write($"{x:X2} ");
        };

        serialPort.Open();

        serialPort.Write("START\r\n");

        Console.ReadKey();
    }
}