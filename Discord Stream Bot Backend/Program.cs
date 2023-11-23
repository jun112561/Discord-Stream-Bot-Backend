using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NLog;
using NLog.Web;
using System;
using System.IO;
using System.Reflection;

namespace Discord_Stream_Bot_Backend
{
    public class Program
    {
        public static string VERSION => GetLinkerTime(Assembly.GetEntryAssembly());
        public static void Main(string[] args)
        {
            var logger = LogManager.Setup().LoadConfigurationFromAppSettings().GetCurrentClassLogger();
            try
            {
                logger.Debug("init main");
                Utility.ServerConfig.InitServerConfig();
                logger.Info(VERSION + " ��l�Ƥ�");

                try
                {
                    RedisConnection.Init(Utility.ServerConfig.RedisOption);
                    Utility.Redis = RedisConnection.Instance.ConnectionMultiplexer;
                    Utility.RedisDb = Utility.Redis.GetDatabase(1);
                    Utility.RedisSub = Utility.Redis.GetSubscriber();

                    Utility.RedisSub.Subscribe(new StackExchange.Redis.RedisChannel("member.syncRedisToken", StackExchange.Redis.RedisChannel.PatternMode.Literal), (channel, value) =>
                    {
                        if (!value.HasValue || string.IsNullOrEmpty(value))
                            return;

                        logger.Info($"������s��{nameof(ServerConfig.RedisTokenKey)}");

                        Utility.ServerConfig.RedisTokenKey = value.ToString();

                        try { File.WriteAllText("server_config.json", JsonConvert.SerializeObject(Utility.ServerConfig, Formatting.Indented)); }
                        catch (Exception ex)
                        {
                            logger.Error($"�]�w�ɫO�s����: {ex}");
                            logger.Error($"�Ф�ʱN���r���J�]�w�ɤ��� \"{nameof(ServerConfig.RedisTokenKey)}\" ���: {value.ToString()}");
                            Environment.Exit(3);
                        }
                    });

                    logger.Info("Redis�w�s�u");
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Redis�s�u���~�A�нT�{���A���O�_�w�}��\r\n");
                    return;
                }

                CreateHostBuilder(args).Build().Run();
            }
            catch (Exception exception)
            {
                //NLog: catch setup errors
                logger.Error(exception, "Stopped program because of exception\r\n");
                throw;
            }
            finally
            {
                // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
                LogManager.Shutdown();
                Utility.RedisSub.UnsubscribeAll();
                Utility.Redis.Dispose();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                })
                .UseNLog();
        }

        public static string GetLinkerTime(Assembly assembly)
        {
            const string BuildVersionMetadataPrefix = "+build";

            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attribute?.InformationalVersion != null)
            {
                var value = attribute.InformationalVersion;
                var index = value.IndexOf(BuildVersionMetadataPrefix);
                if (index > 0)
                {
                    value = value[(index + BuildVersionMetadataPrefix.Length)..];
                    return value;
                }
            }
            return default;
        }
    }
}
