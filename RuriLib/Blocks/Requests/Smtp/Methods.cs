﻿using MailKit.Net.Smtp;
using MailKit.Net.Proxy;
using RuriLib.Attributes;
using RuriLib.Functions.Http;
using RuriLib.Functions.Smtp;
using RuriLib.Http.Models;
using RuriLib.Logging;
using RuriLib.Models.Bots;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using MimeKit;
using System.Linq;
using RuriLib.Extensions;
using RuriLib.Functions.Networking;
using MailKit;
using System.Text;

namespace RuriLib.Blocks.Requests.Smtp
{
    [BlockCategory("SMTP", "Blocks for working with the SMTP protocol", "#b5651d", "#fff")]
    public static class Methods
    {
        private static readonly object hostsLocker = new();
        private static ConcurrentDictionary<string, List<HostEntry>> hosts;
        private static bool initialized = false;
        private static readonly List<string> subdomains = new() { "mail", "smtp-mail", "outbound", "out", "mx", "smtp", "smtps", "m" };
        private static readonly string hosterFile = "UserData/smtpdomains.dat";

        [Block("Connects to a SMTP server by automatically detecting the host and port")]
        public static async Task SmtpAutoConnect(BotData data, string email, int timeoutMilliseconds = 60000)
        {
            data.Logger.LogHeader();

            var ms = new MemoryStream();
            var protocolLogger = new ProtocolLogger(ms, true);
            data.Objects["smtpLoggerStream"] = ms;
            data.Objects["smtpLogger"] = protocolLogger;

            var client = new SmtpClient(protocolLogger)
            {
                Timeout = timeoutMilliseconds,
                ServerCertificateValidationCallback = (s, c, h, e) => true
            };

            if (data.UseProxy && data.Proxy != null)
            {
                client.ProxyClient = MapProxyClient(data);
            }

            data.Objects["smtpClient"] = client;

            var domain = email.Split('@')[1];
            List<HostEntry> candidates = new();

            // Load the dictionary if not initialized (only do this once)
            lock (hostsLocker)
            {
                if (!initialized)
                {
                    hosts = new ConcurrentDictionary<string, List<HostEntry>>(StringComparer.OrdinalIgnoreCase);

                    if (!File.Exists(hosterFile))
                    {
                        File.WriteAllText(hosterFile, string.Empty);
                    }

                    var lines = File.ReadAllLines(hosterFile);

                    foreach (var line in lines)
                    {
                        try
                        {
                            var split = line.Split(':');
                            var entry = new HostEntry(split[1], int.Parse(split[2]));

                            // If we already added an entry for this domain, add it to the list
                            if (hosts.ContainsKey(split[0]))
                            {
                                hosts[split[0]].Add(entry);
                            }
                            else
                            {
                                hosts[split[0]] = new List<HostEntry> { entry };
                            }
                        }
                        catch
                        {

                        }
                    }

                    initialized = true;
                }
            }

            // Try the entries from smtpdomains.dat
            if (hosts.ContainsKey(domain))
            {
                candidates = hosts[domain];
            }

            foreach (var c in candidates)
            {
                var success = await TryConnect(data, client, domain, c);

                if (success)
                {
                    return;
                }
            }

            // Thunderbird autoconfig
            candidates.Clear();
            var thunderbirdUrl = $"{"https"}://live.mozillamessaging.com/autoconfig/v1.1/{domain}";
            try
            {
                var xml = await GetString(data, thunderbirdUrl);
                candidates = SmtpAutoconfig.Parse(xml);
                data.Logger.Log($"Queried {thunderbirdUrl} and got {candidates.Count} server(s)", LogColors.LightBrown);
            }
            catch
            {
                data.Logger.Log($"Failed to query {thunderbirdUrl}", LogColors.LightBrown);
            }

            foreach (var c in candidates)
            {
                var success = await TryConnect(data, client, domain, c);

                if (success)
                {
                    return;
                }
            }

            // Site autoconfig
            candidates.Clear();
            var autoconfigUrl = $"{"https"}://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={email}";
            var autoconfigUrlUnsecure = $"{"http"}://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={email}";
            try
            {
                string xml;

                try
                {
                    xml = await GetString(data, autoconfigUrl);
                }
                catch
                {
                    xml = await GetString(data, autoconfigUrlUnsecure);
                }

                candidates = SmtpAutoconfig.Parse(xml);
                data.Logger.Log($"Queried {autoconfigUrl} and got {candidates.Count} server(s)", LogColors.LightBrown);
            }
            catch
            {
                data.Logger.Log($"Failed to query {autoconfigUrl} (both https and http)", LogColors.LightBrown);
            }

            foreach (var c in candidates)
            {
                var success = await TryConnect(data, client, domain, c);

                if (success)
                {
                    return;
                }
            }

            // Site well-known
            candidates.Clear();
            var wellKnownUrl = $"{"https"}://{domain}/.well-known/autoconfig/mail/config-v1.1.xml";
            var wellKnownUrlUnsecure = $"{"http"}://{domain}/.well-known/autoconfig/mail/config-v1.1.xml";
            try
            {
                string xml;

                try
                {
                    xml = await GetString(data, wellKnownUrl);
                }
                catch
                {
                    xml = await GetString(data, wellKnownUrlUnsecure);
                }

                candidates = SmtpAutoconfig.Parse(xml);
                data.Logger.Log($"Queried {wellKnownUrl} and got {candidates.Count} server(s)", LogColors.LightBrown);
            }
            catch
            {
                data.Logger.Log($"Failed to query {wellKnownUrl} (both https and http)", LogColors.LightBrown);
            }

            foreach (var c in candidates)
            {
                var success = await TryConnect(data, client, domain, c);

                if (success)
                {
                    return;
                }
            }

            // Try MX records
            candidates.Clear();
            try
            {
                var mxRecords = await DnsLookup.FromGoogle(domain, "MX", data.Proxy, 30000, data.CancellationToken);
                mxRecords.ForEach(r =>
                {
                    candidates.Add(new HostEntry(r, 465));
                    candidates.Add(new HostEntry(r, 587));
                    candidates.Add(new HostEntry(r, 25));
                });

                data.Logger.Log($"Queried the MX records and got {candidates.Count} server(s)", LogColors.LightBrown);
            }
            catch
            {
                data.Logger.Log($"Failed to query the MX records", LogColors.LightBrown);
            }

            foreach (var c in candidates)
            {
                var success = await TryConnect(data, client, domain, c);

                if (success)
                {
                    return;
                }
            }

            // Try the domain itself and possible subdomains
            candidates.Clear();
            candidates.Add(new HostEntry(domain, 465));
            candidates.Add(new HostEntry(domain, 587));
            candidates.Add(new HostEntry(domain, 25));

            foreach (var sub in subdomains)
            {
                candidates.Add(new HostEntry($"{sub}.{domain}", 465));
                candidates.Add(new HostEntry($"{sub}.{domain}", 587));
                candidates.Add(new HostEntry($"{sub}.{domain}", 25));
            }

            foreach (var c in candidates)
            {
                var success = await TryConnect(data, client, domain, c);

                if (success)
                {
                    return;
                }
            }

            throw new Exception("Exhausted all possibilities, failed to connect!");
        }

        private static async Task<bool> TryConnect(BotData data, SmtpClient client, string domain, HostEntry entry)
        {
            data.Logger.Log($"Trying {entry.Host} on port {entry.Port}...", LogColors.LightBrown);

            try
            {
                await client.ConnectAsync(entry.Host, entry.Port, MailKit.Security.SecureSocketOptions.Auto, data.CancellationToken);
                data.Logger.Log($"Connected! SSL/TLS: {client.IsSecure}", LogColors.LightBrown);
                
                if (!client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                {
                    data.Logger.Log($"Server doesn't support authentication, trying another one...");
                    return false;
                }

                if (!hosts.ContainsKey(domain))
                {
                    hosts[domain] = new List<HostEntry> { entry };

                    try
                    {
                        File.AppendAllText(hosterFile, $"{domain}:{entry.Host}:{entry.Port}{Environment.NewLine}");
                    }
                    catch
                    {

                    }
                }

                return true;
            }
            catch
            {
                data.Logger.Log($"Failed!", LogColors.LightBrown);
            }

            return false;
        }

        private static async Task<string> GetString(BotData data, string url)
        {
            using var httpClient = HttpFactory.GetRLHttpClient(data.Proxy, new()
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(30000),
                ReadWriteTimeout = TimeSpan.FromMilliseconds(30000)
            });

            using var request = new HttpRequest
            {
                Uri = new Uri(url),
            };

            using var response = await httpClient.SendAsync(request, data.CancellationToken);
            return await response.Content.ReadAsStringAsync(data.CancellationToken);
        }

        [Block("Connects to a SMTP server")]
        public static async Task SmtpConnect(BotData data, string host, int port, int timeoutMilliseconds = 60000)
        {
            data.Logger.LogHeader();

            var client = new SmtpClient
            {
                Timeout = timeoutMilliseconds
            };

            if (data.UseProxy && data.Proxy != null)
            {
                client.ProxyClient = MapProxyClient(data);
            }

            data.Objects["smtpClient"] = client;

            await client.ConnectAsync(host, port, MailKit.Security.SecureSocketOptions.Auto, data.CancellationToken);
            data.Logger.Log($"Connected to {host} on port {port}. SSL/TLS: {client.IsSecure}", LogColors.LightBrown);
        }

        [Block("Disconnects from a SMTP server")]
        public static async Task SmtpDisconnect(BotData data)
        {
            data.Logger.LogHeader();

            var client = GetClient(data);

            if (client.IsConnected)
            {
                await client.DisconnectAsync(true, data.CancellationToken);
                data.Logger.Log($"Client disconnected", LogColors.LightBrown);
            }
            else
            {
                data.Logger.Log($"The client was not connected", LogColors.LightBrown);
            }
        }

        [Block("Logs into an account")]
        public static async Task SmtpLogin(BotData data, string email, string password)
        {
            data.Logger.LogHeader();

            var client = GetClient(data);
            using var logger = client.ProtocolLogger;
            client.AuthenticationMechanisms.Remove("XOAUTH2");
            await client.AuthenticateAsync(email, password, data.CancellationToken);
            data.Logger.Log("Authenticated successfully", LogColors.LightBrown);
        }

        [Block("Gets the protocol log", name = "Get Smtp Log")]
        public static string SmtpGetLog(BotData data)
        {
            data.Logger.LogHeader();

            var protocolLogger = (ProtocolLogger)data.Objects["smtpLogger"];
            var bytes = (protocolLogger.Stream as MemoryStream).ToArray();
            var log = Encoding.UTF8.GetString(bytes);

            data.Logger.Log(log, LogColors.LightBrown);

            return log;
        }

        [Block("Sends a mail to the recipient")]
        public static async Task SmtpSendMail(BotData data, string senderName, string senderAddress,
            string recipientName, string recipientAddress, string subject, string textBody, string htmlBody)
        {
            data.Logger.LogHeader();

            var client = GetAuthenticatedClient(data);

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(senderName, senderAddress));
            message.To.Add(new MailboxAddress(recipientName, recipientAddress));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = htmlBody.Unescape(),
                TextBody = textBody.Unescape()
            };

            message.Body = bodyBuilder.ToMessageBody();

            await client.SendAsync(message, data.CancellationToken);

            data.Logger.Log($"Email sent to {recipientAddress} ({recipientName})", LogColors.LightBrown);
        }

        [Block("Sends a mail in advanced mode", name = "Smtp Send Mail (Advanced)", 
            extraInfo = "Senders/Recipients in the format name: address. For attachments, path to one file per line.")]
        public static async Task SmtpSendMailAdvanced(BotData data, Dictionary<string, string> senders,
            Dictionary<string, string> recipients, string subject, string textBody, string htmlBody,
            Dictionary<string, string> customHeaders, List<string> fileAttachments)
        {
            data.Logger.LogHeader();

            var client = GetAuthenticatedClient(data);

            var message = new MimeMessage();
            message.From.AddRange(senders.Select(s => new MailboxAddress(s.Key, s.Value)));
            message.To.AddRange(recipients.Select(r => new MailboxAddress(r.Key, r.Value)));
            message.Subject = subject;

            foreach (var header in customHeaders)
            {
                message.Headers.Add(header.Key, header.Value);
            }

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = htmlBody.Unescape(),
                TextBody = textBody.Unescape()
            };

            foreach (var file in fileAttachments)
            {
                await bodyBuilder.Attachments.AddAsync(file, data.CancellationToken);
            }

            message.Body = bodyBuilder.ToMessageBody();

            await client.SendAsync(message, data.CancellationToken);

            data.Logger.Log($"Email sent to {recipients.Count} recipients", LogColors.LightBrown);
        }

        private static SmtpClient GetClient(BotData data)
        {
            try
            {
                return (SmtpClient)data.Objects["smtpClient"];
            }
            catch
            {
                throw new Exception("Connect the SMTP client first!");
            }
        }

        private static SmtpClient GetAuthenticatedClient(BotData data)
        {
            var client = GetClient(data);

            if (!client.IsAuthenticated)
            {
                throw new Exception("Authenticate the SMTP client first!");
            }

            return client;
        }

        private static IProxyClient MapProxyClient(BotData data)
        {
            if (data.Proxy.NeedsAuthentication)
            {
                var creds = new NetworkCredential(data.Proxy.Username, data.Proxy.Password);

                return data.Proxy.Type switch
                {
                    Models.Proxies.ProxyType.Http => new HttpProxyClient(data.Proxy.Host, data.Proxy.Port, creds),
                    Models.Proxies.ProxyType.Socks4 => new Socks4Client(data.Proxy.Host, data.Proxy.Port, creds),
                    Models.Proxies.ProxyType.Socks4a => new Socks4aClient(data.Proxy.Host, data.Proxy.Port, creds),
                    Models.Proxies.ProxyType.Socks5 => new Socks5Client(data.Proxy.Host, data.Proxy.Port, creds),
                    _ => throw new NotImplementedException(),
                };
            }
            else
            {
                return data.Proxy.Type switch
                {
                    Models.Proxies.ProxyType.Http => new HttpProxyClient(data.Proxy.Host, data.Proxy.Port),
                    Models.Proxies.ProxyType.Socks4 => new Socks4Client(data.Proxy.Host, data.Proxy.Port),
                    Models.Proxies.ProxyType.Socks4a => new Socks4aClient(data.Proxy.Host, data.Proxy.Port),
                    Models.Proxies.ProxyType.Socks5 => new Socks5Client(data.Proxy.Host, data.Proxy.Port),
                    _ => throw new NotImplementedException(),
                };
            }
        }
    }
}
