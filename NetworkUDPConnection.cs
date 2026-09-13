using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Orbit_Us
{
    public class NetworkUDPConnection
    {
        private UdpClient udpClient;

        public async Task<string> SendTestPacket(
            string address,
            int port)
        {
            udpClient?.Close();

            udpClient = new UdpClient();

            Console.WriteLine(
                $"Sending UDP packet to {address}:{port}"
            );

            string message =
                $"UDP test: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}";

            byte[] data =
                Encoding.UTF8.GetBytes(message);

            await udpClient.SendAsync(
                data,
                data.Length,
                address,
                port
            );

            Console.WriteLine(
                $"UDP packet sent: {message}"
            );

            UdpReceiveResult result =
                await udpClient.ReceiveAsync();

            string response =
                Encoding.UTF8.GetString(
                    result.Buffer
                );

            Console.WriteLine(
                $"Received UDP response from {result.RemoteEndPoint}"
            );

            Console.WriteLine(
                $"UDP response data: {response}"
            );

            return response;
        }

        public void Disconnect()
        {
            try
            {
                udpClient?.Close();
            }
            catch
            {
            }

            udpClient = null;
        }
    }
}