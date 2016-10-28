using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace AdbTest
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                Adb.EnsureServerStarted("adb.exe");

                var devices = Adb.ListDevices();
                foreach (var device in devices)
                    Console.WriteLine(device.ToString());

                var deviceListener = new DeviceListener();
                deviceListener.DeviceAttached += (object sender, DeviceListener.DeviceEventArgs a) =>
                {
                    Console.WriteLine("attached: " + a.Device.ToString());
                    Console.WriteLine(string.Format("response:\n---8<---\n{0}\n---8<---", Adb.Shell(a.Device, "ls")));
                    Adb.Forward(a.Device, "tcp:1337", "tcp:1337");

                    EndPoint endPoint = new IPEndPoint(IPAddress.Loopback, 1337);
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.Connect(endPoint);
                    var stream = new NetworkStream(socket);

                    var streamReader = new StreamReader(stream);
                    var hello = streamReader.ReadLine();
                    Console.WriteLine("phone says: {0}", hello);
                    var streamWriter = new StreamWriter(stream);
                    streamWriter.WriteLine("HELLO TO YOU TOO, PHONE!\n");
                    streamWriter.Flush();
                };
                deviceListener.DeviceDetached += (object sender, DeviceListener.DeviceEventArgs a) => Console.WriteLine("detached: " + a.Device.ToString());

                while (true)
                    deviceListener.Poll();
            }
            catch (Exception e)
            {
                Console.WriteLine("fatal exception: {0}", e.ToString());
            }
        }
    }
}
