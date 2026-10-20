using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MajDHTProber.Models;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>Loads and saves <see cref="ProberSettings"/> as indented JSON.</summary>
    public sealed class SettingsStore
    {
        /// <summary>The serializer options: indented output and enum names instead of numbers.</summary>
        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>The diagnostics logger.</summary>
        private readonly ILogger _logger;

        /// <summary>Creates a store for one settings file.</summary>
        /// <param name="filePath">The settings file path; its directory is created on save.</param>
        /// <param name="logger">The diagnostics logger.</param>
        public SettingsStore(string filePath, ILogger logger)
        {
            FilePath = filePath;
            _logger = logger;
        }

        /// <summary>Gets the settings file path.</summary>
        public string FilePath { get; }

        /// <summary>Loads the settings, falling back to defaults when the file is missing or unreadable.</summary>
        /// <returns>The loaded or default settings.</returns>
        public ProberSettings Load()
        {
            if (!File.Exists(FilePath))
            {
                _logger.LogInformation("未找到设置文件，使用默认设置。");
                return new ProberSettings();
            }
            try
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<ProberSettings>(json, s_jsonOptions) ?? new ProberSettings();
                settings.BootstrapEndpoints ??= new List<string>();
                _logger.LogDebug("已加载设置文件 {FileName}。", Path.GetFileName(FilePath));
                return settings;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "设置文件无法读取，已改用默认设置。");
                return new ProberSettings();
            }
        }

        /// <summary>Saves the settings, replacing the previous file only after the new content was written completely.</summary>
        /// <param name="settings">The settings to persist.</param>
        /// <exception cref="IOException">The file could not be written.</exception>
        /// <exception cref="UnauthorizedAccessException">The directory is not writable.</exception>
        public void Save(ProberSettings settings)
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, s_jsonOptions));
            File.Move(temporary, FilePath, true);
            _logger.LogInformation("设置已保存。");
        }
    }
}
