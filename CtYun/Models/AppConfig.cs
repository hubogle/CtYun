using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CtYun.Models
{
    public class AppConfig
    {
        [JsonPropertyName("accounts")]
        public List<AccountConfig> Accounts { get; set; } = [];

        [JsonPropertyName("keepAliveSeconds")]
        public int KeepAliveSeconds { get; set; } = 60;

        /// <summary>重连周期随机抖动百分比，0 表示固定周期。</summary>
        [JsonPropertyName("keepAliveJitterPercent")]
        public int KeepAliveJitterPercent { get; set; } = 0;

        /// <summary>睡眠时段，格式 HH:mm-HH:mm，多个用逗号分隔，例如 "02:00-07:00,12:30-13:00"。</summary>
        [JsonPropertyName("sleepWindow")]
        public string SleepWindow { get; set; }

        /// <summary>睡眠结束后随机推迟 0~N 分钟再恢复，0 表示准时恢复。</summary>
        [JsonPropertyName("sleepJitterMinutes")]
        public int SleepJitterMinutes { get; set; } = 10;

    }

    public class AccountConfig
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("user")]
        public string User { get; set; }

        [JsonPropertyName("password")]
        public string Password { get; set; }

        [JsonPropertyName("deviceCode")]
        public string DeviceCode { get; set; }
    }
}
