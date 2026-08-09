using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.Utilities.Tests
{
    [TestClass()]
    public class DownloadTests
    {
        [TestMethod()]
        [TestCategory("Integration")]
        public void getFilefromNetTest()
        {
            if (Utilities.Download.getFilefromNet("https://www.google.com/", Path.GetTempFileName()))
                return;

            Assert.Fail();
        }

        [TestMethod()]
        [TestCategory("Integration")]
        public void CheckHTTPFileExists()
        {
            if (Utilities.Download.CheckHTTPFileExists("https://github.com/ArduPilot/MissionPlanner/releases/download/betarelease/MissionPlannerBeta.zip"))
                return;

            Assert.Fail();
        }

        [TestMethod()]
        [TestCategory("Integration")]
        public void GetFileSize()
        {
            if (Utilities.Download.GetFileSize("https://github.com/ArduPilot/MissionPlanner/releases/download/betarelease/MissionPlannerBeta.zip") > 0)
                return;

            Assert.Fail();
        }

        [TestMethod]
        public void DownloadHelpersWorkAgainstLoopbackServer()
        {
            byte[] payload = Encoding.UTF8.GetBytes("DIMP download regression payload");
            string target = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");

            using (var server = new LoopbackHttpServer(payload, 3))
            {
                try
                {
                    Assert.IsTrue(Utilities.Download.getFilefromNet(server.Url, target));
                    CollectionAssert.AreEqual(payload, File.ReadAllBytes(target));
                    Assert.IsTrue(Utilities.Download.CheckHTTPFileExists(server.Url));
                    Assert.AreEqual(payload.Length, Utilities.Download.GetFileSize(server.Url));
                }
                finally
                {
                    File.Delete(target);
                    File.Delete(target + ".new");
                }
            }
        }

        private sealed class LoopbackHttpServer : IDisposable
        {
            private readonly byte[] payload;
            private readonly int requestLimit;
            private readonly TcpListener listener;
            private readonly Thread thread;
            private Exception failure;

            internal LoopbackHttpServer(byte[] payload, int requestLimit)
            {
                this.payload = payload;
                this.requestLimit = requestLimit;
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Url = "http://127.0.0.1:" + port + "/payload";
                thread = new Thread(Serve) { IsBackground = true };
                thread.Start();
            }

            internal string Url { get; }

            private void Serve()
            {
                try
                {
                    for (int request = 0; request < requestLimit; request++)
                    {
                        using (TcpClient client = listener.AcceptTcpClient())
                        using (NetworkStream stream = client.GetStream())
                        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                        {
                            string requestLine = reader.ReadLine() ?? string.Empty;
                            string line;
                            do
                            {
                                line = reader.ReadLine();
                            } while (!string.IsNullOrEmpty(line));

                            bool isHead = requestLine.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase);
                            byte[] headers = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Length: " + payload.Length +
                                "\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
                            stream.Write(headers, 0, headers.Length);
                            if (!isHead)
                            {
                                stream.Write(payload, 0, payload.Length);
                            }
                        }
                    }
                }
                catch (SocketException) when (!listener.Server.IsBound)
                {
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            public void Dispose()
            {
                listener.Stop();
                thread.Join(TimeSpan.FromSeconds(5));
                if (failure != null)
                {
                    Assert.Fail("Loopback HTTP server failed: " + failure);
                }
            }
        }
    }
}
