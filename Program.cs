using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;

namespace AdbTest
{
    public class Device
    {
        public Device(string serial, string product, string model, string device)
        {
            Serial = serial;
            Product = product;
            Model = model;
            DeviceString = device;
        }

        public readonly string Serial;
        public readonly string Product;
        public readonly string Model;
        public readonly string DeviceString;

        public override string ToString()
        {
            return "{ Serial: " + Serial
                + ", Product: " + Product
                + ", Model: " + Model
                + ", DeviceString: " + DeviceString
                + " }";
        }
    }

    static class BinaryStreamExtensions
    {
        public static void Expect(this BinaryReader reader, string expected)
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var buffer = reader.ReadBytes(expectedBytes.Length);

            if (!expectedBytes.SequenceEqual(buffer))
                throw new Exception(string.Format("Expected \"{0}\", got \"{1}\"", expected, Encoding.UTF8.GetString(buffer)));
        }

        public static string ReadResponse(this BinaryReader reader)
        {
            var lengthString = Encoding.UTF8.GetString(reader.ReadBytes(4));
            int length = int.Parse(lengthString, System.Globalization.NumberStyles.HexNumber);
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }
    }

    class Adb
    {
        public static NetworkStream ConnectToAdb()
        {
            EndPoint endPoint = new IPEndPoint(IPAddress.Loopback, 5037);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(endPoint);
            return new NetworkStream(socket);
        }

        public static byte[] FormatAdbMessage(string message)
        {
            return Encoding.UTF8.GetBytes(string.Format("{0:X4}{1}", message.Length, message));
        }

        public static NetworkStream TrackDevices()
        {
            var stream = Adb.ConnectToAdb();

            var streamWriter = new BinaryWriter(stream);
            streamWriter.Write(Adb.FormatAdbMessage("host:track-devices"));
            streamWriter.Flush();

            var streamReader = new BinaryReader(stream);
            streamReader.Expect("OKAY");

            return stream;
        }

        public static Device[] ListDevices()
        {
            var stream = Adb.ConnectToAdb();

            var writer = new BinaryWriter(stream);
            writer.Write(Adb.FormatAdbMessage("host:devices-l"));
            writer.Flush();

            var reader = new BinaryReader(stream);
            reader.Expect("OKAY");
            var response = reader.ReadResponse();

            var ret = new List<Device>();
            foreach (var line in response.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // TODO: be more graceful in the parsing here
                var parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var serial = parts[0];
                var product = parts[2].Split(':')[1];
                var model = parts[3].Split(':')[1];
                var device = parts[4].Split(':')[1];
                ret.Add(new Device(serial, product, model, device));
            }
            return ret.ToArray();
        }
    }

    public class DeviceListener
    {
        readonly NetworkStream _stream;

        public DeviceListener()
        {
            _stream = Adb.TrackDevices();
        }

        public void Poll()
        {
            while (_stream.DataAvailable)
            {
                var reader = new BinaryReader(_stream);
                var response = reader.ReadResponse();
                foreach (var line in response.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Console.WriteLine(string.Format("line: \"{0}\"", line));
/*
                    var parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var identifier = parts[0];
                    var product = parts[2].Split(':')[1];
                    var model = parts[3].Split(':')[1];
                    var device = parts[4].Split(':')[1];
                    ret.Add(new Device(identifier, product, model, device));
 */
                }
            }
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                var devices = Adb.ListDevices();
                foreach (var device in devices)
                    Console.WriteLine(device.ToString());

                var deviceListener = new DeviceListener();
                // deviceListener.DeviceAttached += (object sender, DeviceListener.DeviceEventArgs a) => Console.WriteLine("attached: " + a.Device.ToString());
                // deviceListener.DeviceDetached += (object sender, DeviceListener.DeviceEventArgs a) => Console.WriteLine("detached: " + a.Device.ToString());

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
