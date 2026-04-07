using System.Net;
using System.Net.Sockets;
using Makaretu.Dns;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace Twms2.Server.Services;

public class MdnsOptions
{
    public string Hostname { get; set; } = "twms";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// mDNS 브로드캐스트 서비스.
/// 서버 시작 시 twms.local 호스트명을 로컬 네트워크에 광고하여
/// 같은 망의 PC에서 http://twms.local 로 접속 가능하게 함.
/// </summary>
public class MdnsHostedService : IHostedService, IDisposable
{
    private readonly MdnsOptions _options;
    private readonly IServer _server;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MdnsHostedService> _logger;

    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;

    public MdnsHostedService(
        IOptions<MdnsOptions> options,
        IServer server,
        IHostApplicationLifetime lifetime,
        ILogger<MdnsHostedService> logger)
    {
        _options = options.Value;
        _server = server;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("mDNS 브로드캐스트 비활성화됨");
            return Task.CompletedTask;
        }

        // Kestrel 바인딩 완료 후 mDNS 시작
        _lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                StartMdnsBroadcast();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "mDNS 브로드캐스트 시작 실패 — 서버는 IP 주소로 접속 가능합니다");
                CleanUp();
            }
        });

        return Task.CompletedTask;
    }

    private void StartMdnsBroadcast()
    {
        var hostname = $"{_options.Hostname}.local";
        var port = GetListeningPort();

        _mdns = new MulticastService();

        // twms.local A/AAAA 레코드 쿼리에 직접 응답
        _mdns.QueryReceived += (s, e) =>
        {
            var msg = e.Message;
            if (!msg.Questions.Any(q =>
                    q.Name.ToString().Equals(hostname, StringComparison.OrdinalIgnoreCase)
                    && (q.Type == DnsType.A || q.Type == DnsType.AAAA || q.Type == DnsType.ANY)))
                return;

            var response = msg.CreateResponse();
            foreach (var address in MulticastService.GetIPAddresses())
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    response.Answers.Add(new ARecord
                    {
                        Name = hostname,
                        Address = address,
                        TTL = TimeSpan.FromMinutes(2)
                    });
                }
                else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    response.Answers.Add(new AAAARecord
                    {
                        Name = hostname,
                        Address = address,
                        TTL = TimeSpan.FromMinutes(2)
                    });
                }
            }

            if (response.Answers.Count > 0)
                _mdns.SendAnswer(response);
        };

        _mdns.Start();

        // DNS-SD 서비스 광고 (_http._tcp)
        _sd = new ServiceDiscovery(_mdns);
        var profile = new ServiceProfile(_options.Hostname, "_http._tcp", (ushort)port);
        _sd.Advertise(profile);

        _logger.LogInformation("mDNS 브로드캐스트 시작: {Hostname} (포트 {Port})", hostname, port);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_sd != null)
        {
            _logger.LogInformation("mDNS 브로드캐스트 종료 중...");
            try { _sd.Unadvertise(); } catch { /* 종료 중 에러 무시 */ }
        }
        CleanUp();
        return Task.CompletedTask;
    }

    private int GetListeningPort()
    {
        var addressFeature = _server.Features.Get<IServerAddressesFeature>();
        if (addressFeature != null)
        {
            foreach (var address in addressFeature.Addresses)
            {
                if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
                    return uri.Port;
            }
        }
        return 80;
    }

    private void CleanUp()
    {
        _sd?.Dispose();
        _sd = null;
        _mdns?.Stop();
        _mdns?.Dispose();
        _mdns = null;
    }

    public void Dispose()
    {
        CleanUp();
    }
}
