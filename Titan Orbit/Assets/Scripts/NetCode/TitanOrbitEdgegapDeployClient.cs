using System;
using System.Text;
using System.Threading.Tasks;
using TitanOrbit.Diagnostics;
using UnityEngine;
using UnityEngine.Networking;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Edgegap v2 deploy + self-stop helpers for dedicated overflow. Token and app name/version
    /// come from container env — never from the game client.
    /// </summary>
    public static class TitanOrbitEdgegapDeployClient
    {
        const string DeployUrl = "https://api.edgegap.com/v2/deployments";
        const string ListUrl = "https://api.edgegap.com/v1/deployments?limit=50";

        /// <summary>True when API token and application identity are present.</summary>
        public static bool CanDeploy
        {
            get
            {
                return !string.IsNullOrWhiteSpace(GetToken()) &&
                       !string.IsNullOrWhiteSpace(GetAppName()) &&
                       !string.IsNullOrWhiteSpace(GetAppVersion());
            }
        }

        /// <summary>
        /// Starts a sibling Edgegap container that boots a new dedicated match.
        /// New process publishes its own UGS lobby; caller waits for that listing.
        /// </summary>
        public static async Task<bool> TryDeploySuccessorAsync(string placementIp, bool nextIsLatest)
        {
            if (!CanDeploy)
            {
                Debug.LogWarning("[TitanOrbitEdgegapDeploy] Missing EDGEGAP_API_TOKEN / EDGEGAP_APP_NAME / EDGEGAP_APP_VERSION.");
                DedicatedServerFileLog.Append("edgegap", "Deploy skipped — missing API env");
                return false;
            }

            string ip = string.IsNullOrWhiteSpace(placementIp) ? "1.1.1.1" : placementIp.Trim();
            string isLatest = nextIsLatest ? "1" : "0";
            string json =
                "{\"application\":\"" + Escape(GetAppName()) +
                "\",\"version\":\"" + Escape(GetAppVersion()) +
                "\",\"users\":[{\"user_type\":\"ip_address\",\"user_data\":{\"ip_address\":\"" +
                Escape(ip) +
                "\"}}],\"environment_variables\":[{\"key\":\"TITANORBIT_IS_LATEST\",\"value\":\"" +
                isLatest +
                "\",\"is_hidden\":false}],\"tags\":[\"titan-orbit\",\"overflow\"]}";

            using var req = new UnityWebRequest(DeployUrl, UnityWebRequest.kHttpVerbPOST);
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", "token " + GetToken());
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 30;

            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            bool ok = req.responseCode == 202 || req.responseCode == 200;
            string body = req.downloadHandler != null ? req.downloadHandler.text : "";
            DedicatedServerFileLog.Append(
                "edgegap",
                "Deploy HTTP " + req.responseCode + " ok=" + ok + " body=" + TrimBody(body));
            if (!ok)
                Debug.LogWarning("[TitanOrbitEdgegapDeploy] Deploy failed HTTP " + req.responseCode + " " + TrimBody(body));
            else
                Debug.Log("[TitanOrbitEdgegapDeploy] Successor requested isLatest=" + nextIsLatest);
            return ok;
        }

        /// <summary>Active Edgegap deployments, or -1 when the list call fails.</summary>
        public static async Task<int> TryCountActiveDeploymentsAsync()
        {
            string token = GetToken();
            if (string.IsNullOrWhiteSpace(token))
                return -1;

            using var req = UnityWebRequest.Get(ListUrl);
            req.SetRequestHeader("Authorization", "token " + token);
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 20;
            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success)
                return -1;

            string body = req.downloadHandler != null ? req.downloadHandler.text : "";
            return TryReadJsonInt(body, "total_count");
        }

        /// <summary>
        /// Asks Edgegap to stop this container via injected <c>ARBITRIUM_DELETE_URL</c>.
        /// Safe no-op outside Edgegap.
        /// </summary>
        public static async Task TrySelfStopAsync()
        {
            string url = Environment.GetEnvironmentVariable("ARBITRIUM_DELETE_URL");
            string token = Environment.GetEnvironmentVariable("ARBITRIUM_DELETE_TOKEN");
            if (string.IsNullOrWhiteSpace(url))
                return;

            using var req = UnityWebRequest.Delete(url.Trim());
            if (!string.IsNullOrWhiteSpace(token))
                req.SetRequestHeader("authorization", token.Trim());
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 15;
            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();
            DedicatedServerFileLog.Append(
                "edgegap",
                "Self-stop HTTP " + req.responseCode + " " + req.result);
        }

        static string GetToken()
        {
            string token = Environment.GetEnvironmentVariable("EDGEGAP_API_TOKEN");
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }

        static string GetAppName()
        {
            string name = Environment.GetEnvironmentVariable("EDGEGAP_APP_NAME");
            if (string.IsNullOrWhiteSpace(name))
                name = Environment.GetEnvironmentVariable("TITANORBIT_EDGEGAP_APP");
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }

        static string GetAppVersion()
        {
            string version = Environment.GetEnvironmentVariable("EDGEGAP_APP_VERSION");
            if (string.IsNullOrWhiteSpace(version))
                version = Environment.GetEnvironmentVariable("TITANORBIT_EDGEGAP_VERSION");
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }

        static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static string TrimBody(string body)
        {
            if (string.IsNullOrEmpty(body))
                return "";
            return body.Length <= 240 ? body : body.Substring(0, 240);
        }

        static int TryReadJsonInt(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return -1;
            string needle = "\"" + key + "\"";
            int at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0)
                return -1;
            int colon = json.IndexOf(':', at + needle.Length);
            if (colon < 0)
                return -1;
            int i = colon + 1;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t'))
                i++;
            int start = i;
            while (i < json.Length && json[i] >= '0' && json[i] <= '9')
                i++;
            if (i == start)
                return -1;
            return int.TryParse(json.Substring(start, i - start), out int value) ? value : -1;
        }
    }
}
