using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace RoofingLeadGeneration.Services
{
    /// <summary>
    /// Sends transactional emails via any SMTP server.
    /// Configure in appsettings.json under "Email": { SmtpHost, SmtpPort, Username, Password, FromAddress, FromName }
    /// </summary>
    public class EmailService
    {
        private readonly IConfiguration              _config;
        private readonly ILogger<EmailService>       _logger;

        public EmailService(IConfiguration config, ILogger<EmailService> logger)
        {
            _config = config;
            _logger = logger;
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_config["Email:SmtpHost"]) &&
            !string.IsNullOrWhiteSpace(_config["Email:FromAddress"]);

        public async Task<bool> SendAsync(string toAddress, string subject, string htmlBody)
        {
            if (!IsConfigured)
            {
                _logger.LogWarning("Email not configured — skipping send to {To}", toAddress);
                return false;
            }

            try
            {
                var host        = _config["Email:SmtpHost"]!;
                var port        = int.TryParse(_config["Email:SmtpPort"], out var p) ? p : 587;
                var username    = _config["Email:Username"] ?? "";
                var password    = _config["Email:Password"] ?? "";
                var fromAddress = _config["Email:FromAddress"]!;
                var fromName    = _config["Email:FromName"] ?? "StormLead Pro";

                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(fromName, fromAddress));
                message.To.Add(MailboxAddress.Parse(toAddress));
                message.Subject = subject;
                message.Body    = new TextPart("html") { Text = htmlBody };

                using var client = new SmtpClient();

                // Try STARTTLS first (port 587), fall back to implicit SSL (port 465),
                // and finally plain (port 25 or when creds are absent)
                var secureSocketOptions = port == 465
                    ? SecureSocketOptions.SslOnConnect
                    : SecureSocketOptions.StartTlsWhenAvailable;

                await client.ConnectAsync(host, port, secureSocketOptions);

                if (!string.IsNullOrWhiteSpace(username))
                    await client.AuthenticateAsync(username, password);

                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                _logger.LogInformation("Email sent to {To}: {Subject}", toAddress, subject);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {To}", toAddress);
                return false;
            }
        }
    }
}
