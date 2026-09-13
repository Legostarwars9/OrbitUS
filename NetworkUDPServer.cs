using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Orbit_Us
{
    public class NetworkUDPServer
    {
        private UdpClient udpServer;
        private bool running;

        public void Start(int port)
        {
            Stop();

            udpServer = new UdpClient(port);
            running = true;

            Console.WriteLine(
                $"UDP Server Started on port {port}"
            );

            _ = Listen();
        }

        private async Task Listen()
        {
            while (running)
            {
                try
                {
                    UdpReceiveResult result =
                        await udpServer.ReceiveAsync();

                    string message =
                        Encoding.UTF8.GetString(
                            result.Buffer
                        );

                    Console.WriteLine(
                        $"Received UDP packet from {result.RemoteEndPoint}"
                    );

                    Console.WriteLine(
                        $"UDP data: {message}"
                    );

                    string response =
                        $"UDP response: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}";

                    byte[] responseData =
                        Encoding.UTF8.GetBytes(response);

                    await udpServer.SendAsync(
                        responseData,
                        responseData.Length,
                        result.RemoteEndPoint
                    );

                    Console.WriteLine(
                        "Sent UDP response."
                    );
                }
                catch (Exception ex)
                {
                    if (running)
                    {
                        Console.WriteLine(
                            $"UDP server error: {ex.Message}"
                        );
                    }
                }
            }
        }

        public void Stop()
        {
            running = false;

            try
            {
                udpServer?.Close();
            }
            catch
            {
            }

            udpServer = null;
        }
    }
}