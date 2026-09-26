using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AnimeStudio.GUI
{
    internal static class ReleaseUpdateChecker
    {
        private const string ApiUrl = "https://api.github.com/repos/ssice-a/AnimeStudio/releases/latest";
        private static readonly HttpClient Client = CreateClient();
        private static bool checking;

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EIEM-AnimeStudio-UpdateCheck/1.0");
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        private static bool TryVersion(string tag, out Version version)
        {
            if (!string.IsNullOrEmpty(tag) && (tag[0] == 'v' || tag[0] == 'V'))
                tag = tag.Substring(1);
            if (Version.TryParse(tag, out version) && version.Build >= 0 && version.Revision < 0)
                return true;
            version = null;
            return false;
        }

        internal static async Task CheckAndPromptAsync(IWin32Window owner, bool manual)
        {
            if (checking)
            {
                if (manual) MessageBox.Show(owner, "An update check is already running.",
                    "Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            checking = true;
            try
            {
                using var response = await Client.GetAsync(ApiUrl);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var release = document.RootElement;
                var tag = release.GetProperty("tag_name").GetString();
                var url = release.GetProperty("html_url").GetString();
                if (!TryVersion(tag, out var latest) ||
                    !TryVersion(typeof(Program).Assembly.GetName().Version.ToString(3), out var current) ||
                    !Uri.TryCreate(url, UriKind.Absolute, out var link) ||
                    link.Scheme != Uri.UriSchemeHttps || link.Host != "github.com" ||
                    !link.AbsolutePath.StartsWith("/ssice-a/AnimeStudio/releases/", StringComparison.Ordinal))
                    throw new FormatException("Invalid AnimeStudio release metadata.");

                if (latest <= current)
                {
                    if (manual) MessageBox.Show(owner, "You are using the latest AnimeStudio release.",
                        "Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (!manual && string.Equals(Properties.Settings.Default.ignoredReleaseTag,
                    tag, StringComparison.OrdinalIgnoreCase)) return;
                using var dialog = new Form {
                    Text = "AnimeStudio Update", StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false,
                    MinimizeBox = false, ClientSize = new Size(395, 126)
                };
                dialog.Controls.Add(new Label {
                    Text = $"AnimeStudio {tag} is available (installed: v{current}).",
                    Location = new Point(16, 17), Size = new Size(363, 40)
                });
                var open = new Button { Text = "Open Release", Location = new Point(16, 75), Size = new Size(110, 30) };
                var later = new Button { Text = "Later", Location = new Point(142, 75), Size = new Size(95, 30) };
                var ignore = new Button { Text = "Ignore This Version", Location = new Point(251, 75), Size = new Size(128, 30) };
                open.Click += (_, __) => {
                    Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
                    dialog.Close();
                };
                later.Click += (_, __) => dialog.Close();
                ignore.Click += (_, __) => {
                    Properties.Settings.Default.ignoredReleaseTag = tag;
                    Properties.Settings.Default.Save();
                    dialog.Close();
                };
                dialog.Controls.AddRange(new Control[] { open, later, ignore });
                dialog.ShowDialog(owner);
            }
            catch (Exception error)
            {
                if (manual) MessageBox.Show(owner, error.Message, "Update Check Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { checking = false; }
        }
    }
}
