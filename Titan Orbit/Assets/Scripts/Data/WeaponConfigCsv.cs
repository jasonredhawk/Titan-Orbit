using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>Import/export WeaponConfig to CSV for editing in a spreadsheet.</summary>
    public static class WeaponConfigCsv
    {
        public const string DefaultCsvPath = "Assets/Data/WeaponConfigs.csv";

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>Export a single WeaponConfig to CSV. If file exists, appends a new block with configName.</summary>
        public static void ExportToCsv(WeaponConfig config, string filePath = null)
        {
            if (config == null || config.cannons == null) return;
            filePath ??= DefaultCsvPath;
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("ConfigName,CannonIndex,FireRate,EnergyCostPerShot,DamagePerBullet,DirectionAngle,SpreadType,SpreadAngleMin,SpreadAngleMax,SpreadProjectileCount,BulletScale,LocalOffsetX,LocalOffsetZ,BulletSpeed");
            for (int i = 0; i < config.cannons.Count; i++)
            {
                var c = config.cannons[i];
                sb.AppendLine(string.Join(",",
                    Escape(config.displayName),
                    i.ToString(Invariant),
                    c.fireRate.ToString(Invariant),
                    c.energyCostPerShot.ToString(Invariant),
                    c.damagePerBullet.ToString(Invariant),
                    c.directionAngle.ToString(Invariant),
                    c.spreadType.ToString(),
                    c.spreadAngleMin.ToString(Invariant),
                    c.spreadAngleMax.ToString(Invariant),
                    c.spreadProjectileCount.ToString(Invariant),
                    c.bulletScale.ToString(Invariant),
                    c.localOffsetX.ToString(Invariant),
                    c.localOffsetZ.ToString(Invariant),
                    c.bulletSpeed.ToString(Invariant)
                ));
            }
            File.WriteAllText(filePath, sb.ToString());
            Debug.Log($"Exported weapon config '{config.displayName}' to {filePath}");
        }

        /// <summary>Export multiple configs to one CSV. First column is ConfigName so you can have multiple blocks.</summary>
        public static void ExportAllToCsv(IList<WeaponConfig> configs, string filePath = null)
        {
            if (configs == null || configs.Count == 0) return;
            filePath ??= DefaultCsvPath;
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("ConfigName,CannonIndex,FireRate,EnergyCostPerShot,DamagePerBullet,DirectionAngle,SpreadType,SpreadAngleMin,SpreadAngleMax,SpreadProjectileCount,BulletScale,LocalOffsetX,LocalOffsetZ,BulletSpeed");
            foreach (var config in configs)
            {
                if (config?.cannons == null) continue;
                for (int i = 0; i < config.cannons.Count; i++)
                {
                    var c = config.cannons[i];
                    sb.AppendLine(string.Join(",",
                        Escape(config.displayName),
                        i.ToString(Invariant),
                        c.fireRate.ToString(Invariant),
                        c.energyCostPerShot.ToString(Invariant),
                        c.damagePerBullet.ToString(Invariant),
                        c.directionAngle.ToString(Invariant),
                        c.spreadType.ToString(),
                        c.spreadAngleMin.ToString(Invariant),
                        c.spreadAngleMax.ToString(Invariant),
                        c.spreadProjectileCount.ToString(Invariant),
                        c.bulletScale.ToString(Invariant),
                        c.localOffsetX.ToString(Invariant),
                        c.localOffsetZ.ToString(Invariant),
                        c.bulletSpeed.ToString(Invariant)
                    ));
                }
            }
            File.WriteAllText(filePath, sb.ToString());
            Debug.Log($"Exported {configs.Count} weapon configs to {filePath}");
        }

        /// <summary>Import from CSV. Parses ConfigName and creates one WeaponConfig per unique name (or updates existing if passed).</summary>
        public static List<WeaponConfig> ImportFromCsv(string filePath = null)
        {
            filePath ??= DefaultCsvPath;
            if (!File.Exists(filePath))
            {
                Debug.LogWarning($"WeaponConfig CSV not found: {filePath}");
                return new List<WeaponConfig>();
            }
            var lines = File.ReadAllLines(filePath);
            if (lines.Length < 2) return new List<WeaponConfig>();

            var configsByName = new Dictionary<string, WeaponConfig>();
            string[] headers = ParseCsvLine(lines[0]);
            int idxConfigName = IndexOf(headers, "ConfigName");
            int idxFireRate = IndexOf(headers, "FireRate");
            int idxEnergyCost = IndexOf(headers, "EnergyCostPerShot");
            int idxDamage = IndexOf(headers, "DamagePerBullet");
            int idxDirectionAngle = IndexOf(headers, "DirectionAngle");
            int idxSpreadType = IndexOf(headers, "SpreadType");
            int idxSpreadMin = IndexOf(headers, "SpreadAngleMin");
            int idxSpreadMax = IndexOf(headers, "SpreadAngleMax");
            int idxSpreadProjectileCount = IndexOf(headers, "SpreadProjectileCount");
            int idxBulletScale = IndexOf(headers, "BulletScale");
            int idxOffsetX = IndexOf(headers, "LocalOffsetX");
            int idxOffsetZ = IndexOf(headers, "LocalOffsetZ");
            int idxBulletSpeed = IndexOf(headers, "BulletSpeed");

            for (int i = 1; i < lines.Length; i++)
            {
                string[] cols = ParseCsvLine(lines[i]);
                if (cols.Length == 0) continue;
                string configName = idxConfigName >= 0 && idxConfigName < cols.Length ? Unescape(cols[idxConfigName]) : "Weapon";
                if (!configsByName.TryGetValue(configName, out WeaponConfig config))
                {
                    config = ScriptableObject.CreateInstance<WeaponConfig>();
                    config.displayName = configName;
                    config.cannons = new List<CannonConfig>();
                    configsByName.Add(configName, config);
                }
                var cannon = new CannonConfig();
                if (idxFireRate >= 0 && idxFireRate < cols.Length) float.TryParse(cols[idxFireRate], NumberStyles.Float, Invariant, out cannon.fireRate);
                if (idxEnergyCost >= 0 && idxEnergyCost < cols.Length) float.TryParse(cols[idxEnergyCost], NumberStyles.Float, Invariant, out cannon.energyCostPerShot);
                if (idxDamage >= 0 && idxDamage < cols.Length) float.TryParse(cols[idxDamage], NumberStyles.Float, Invariant, out cannon.damagePerBullet);
                if (idxDirectionAngle >= 0 && idxDirectionAngle < cols.Length) float.TryParse(cols[idxDirectionAngle], NumberStyles.Float, Invariant, out cannon.directionAngle);
                if (idxSpreadType >= 0 && idxSpreadType < cols.Length) Enum.TryParse(cols[idxSpreadType], true, out cannon.spreadType);
                if (idxSpreadMin >= 0 && idxSpreadMin < cols.Length) float.TryParse(cols[idxSpreadMin], NumberStyles.Float, Invariant, out cannon.spreadAngleMin);
                if (idxSpreadMax >= 0 && idxSpreadMax < cols.Length) float.TryParse(cols[idxSpreadMax], NumberStyles.Float, Invariant, out cannon.spreadAngleMax);
                if (idxSpreadProjectileCount >= 0 && idxSpreadProjectileCount < cols.Length) int.TryParse(cols[idxSpreadProjectileCount], NumberStyles.Integer, Invariant, out cannon.spreadProjectileCount);
                if (idxBulletScale >= 0 && idxBulletScale < cols.Length) float.TryParse(cols[idxBulletScale], NumberStyles.Float, Invariant, out cannon.bulletScale);
                if (idxOffsetX >= 0 && idxOffsetX < cols.Length) float.TryParse(cols[idxOffsetX], NumberStyles.Float, Invariant, out cannon.localOffsetX);
                if (idxOffsetZ >= 0 && idxOffsetZ < cols.Length) float.TryParse(cols[idxOffsetZ], NumberStyles.Float, Invariant, out cannon.localOffsetZ);
                if (idxBulletSpeed >= 0 && idxBulletSpeed < cols.Length) float.TryParse(cols[idxBulletSpeed], NumberStyles.Float, Invariant, out cannon.bulletSpeed);
                config.cannons.Add(cannon);
            }
            return new List<WeaponConfig>(configsByName.Values);
        }

        private static int IndexOf(string[] arr, string name)
        {
            for (int i = 0; i < arr.Length; i++)
                if (string.Equals(arr[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static string[] ParseCsvLine(string line)
        {
            var list = new List<string>();
            var cur = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }
                if (!inQuotes && c == ',')
                {
                    list.Add(cur.ToString().Trim());
                    cur.Clear();
                    continue;
                }
                cur.Append(c);
            }
            list.Add(cur.ToString().Trim());
            return list.ToArray();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.StartsWith("\"") && s.EndsWith("\"") && s.Length >= 2)
                return s.Substring(1, s.Length - 2).Replace("\"\"", "\"");
            return s;
        }
    }
}
