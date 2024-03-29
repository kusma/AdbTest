﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AdbTest
{
    public struct Device
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

        public static byte[] ReadAllBytes(this BinaryReader reader)
        {
            using (var memoryStream = new MemoryStream())
            {
                var buffer = new byte[4096];
                while (true)
                {
                    int read = reader.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;

                    memoryStream.Write(buffer, 0, read);
                }
                return memoryStream.ToArray();
            }
        }
    }

    static class Adb
    {
        static readonly EndPoint EndPoint = new IPEndPoint(IPAddress.Loopback, 5037);

        public static NetworkStream ConnectToAdb()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(EndPoint);
            return new NetworkStream(socket);
        }

        public static void EnsureServerStarted(string executablePath)
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                try
                {
                    socket.Connect(EndPoint);
                    socket.Close();
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode != SocketError.ConnectionRefused)
                        throw;

                    StartServer(executablePath);
                }
            }
        }

        public static void StartServer(string executablePath)
        {
            using (Process proc = new Process())
            {
                proc.StartInfo = new ProcessStartInfo()
                {
                    CreateNoWindow = false,
                    UseShellExecute = false,
                    FileName = executablePath,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    Arguments = "start-server",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                proc.Start();
                proc.StandardInput.Close();
                var stdErr = proc.StandardError.ReadToEnd();
                var stdOut = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw new Exception("adb failed: " + stdErr);
            }
        }

        static byte[] FormatAdbMessage(string message)
        {
            return Encoding.UTF8.GetBytes(string.Format("{0:X4}{1}", message.Length, message));
        }

        static Stream TransportSerial(string serial)
        {
            var stream = ConnectToAdb();

            var writer = new BinaryWriter(stream);
            writer.Write(FormatAdbMessage("host:transport:" + serial));
            writer.Flush();

            var reader = new BinaryReader(stream);
            reader.Expect("OKAY");
            return stream;
        }

        public static NetworkStream TrackDevices()
        {
            var stream = ConnectToAdb();

            var writer = new BinaryWriter(stream);
            writer.Write(FormatAdbMessage("host:track-devices"));
            writer.Flush();

            var reader = new BinaryReader(stream);
            reader.Expect("OKAY");

            return stream;
        }

        public static Device[] ListDevices()
        {
            var stream = ConnectToAdb();

            var writer = new BinaryWriter(stream);
            writer.Write(FormatAdbMessage("host:devices-l"));
            writer.Flush();

            var reader = new BinaryReader(stream);
            reader.Expect("OKAY");
            var response = reader.ReadResponse();

            var ret = new List<Device>();
            foreach (var line in response.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var serial = parts[0];
                if (parts[1] == "device")
                {
                    // TODO: be more graceful in the parsing here
                    var product = parts[2].Split(':')[1];
                    var model = parts[3].Split(':')[1];
                    var device = parts[4].Split(':')[1];
                    ret.Add(new Device(serial, product, model, device));
                }
            }
            return ret.ToArray();
        }

        public static string Shell(Device device, string cmd)
        {
            var stream = Adb.TransportSerial(device.Serial);

            var writer = new BinaryWriter(stream);
            writer.Write(Adb.FormatAdbMessage("shell:" + cmd));
            writer.Flush();

            var reader = new BinaryReader(stream);
            reader.Expect("OKAY");

            var response = reader.ReadAllBytes();
            return Encoding.UTF8.GetString(response);
        }

        public static void Forward(Device device, string local, string remote)
        {
            var stream = Adb.ConnectToAdb();

            var writer = new BinaryWriter(stream);
            writer.Write(Adb.FormatAdbMessage(string.Format("host-serial:{0}:forward:{1};{2}", device.Serial, local, remote)));
            writer.Flush();

            var reader = new BinaryReader(stream);
            reader.Expect("OKAY");
        }
    }

    public class DeviceListener
    {
        readonly NetworkStream _stream;
        Device[] _devices;

        public DeviceListener()
        {
            _stream = Adb.TrackDevices();
        }

        public Device[] AttachedDevices { get { return _devices.ToArray(); } }

        public class DeviceEventArgs : EventArgs
        {
            public Device Device { get; internal set; }
        }

        public delegate void DeviceEventHandler(object sender, DeviceEventArgs e);
        public event DeviceEventHandler DeviceAttached;
        public event DeviceEventHandler DeviceDetached;

        public void Poll()
        {
            bool gotResponse = false;
            while (_stream.DataAvailable)
            {
                var reader = new BinaryReader(_stream);
                var response = reader.ReadResponse();
                // The response doesn't contain all the device-properties, so let's just re-fetch the device-list after we're done
                gotResponse = true;
            }

            if (gotResponse)
            {
                var devices = Adb.ListDevices();
                if (_devices != null)
                {
                    // diff the old and new device lists!

                    if (DeviceDetached != null)
                        foreach (var device in _devices)
                            if (devices.All(d => d.Serial != device.Serial))
                                DeviceDetached(this, new DeviceEventArgs() { Device = device });

                    if (DeviceAttached != null)
                        foreach (var device in devices)
                            if (_devices.All(d => d.Serial != device.Serial))
                                DeviceAttached(this, new DeviceEventArgs() { Device = device });
                }
                else
                {
                    // all devices were attached!
                    if (DeviceAttached != null)
                        foreach (var device in devices)
                            DeviceAttached(this, new DeviceEventArgs() { Device = device });
                }
                _devices = devices;
            }
        }
    }
}
