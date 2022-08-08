using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NLog.Web;
using System;
using System.IO;

namespace Discord_Member_Check
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var logger = NLogBuilder.ConfigureNLog("nlog.config").GetCurrentClassLogger();
            try
            {
                logger.Debug("init main");
                Utility.ServerConfig.InitServerConfig();

                try
                {
                    RedisConnection.Init(Utility.ServerConfig.RedisOption);
                    Utility.Redis = RedisConnection.Instance.ConnectionMultiplexer;
                    Utility.RedisDb = Utility.Redis.GetDatabase(1);
                    Utility.RedisSub = Utility.Redis.GetSubscriber();

                    Utility.RedisSub.Subscribe("member.syncRedisToken", (channel, value) =>
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
                NLog.LogManager.Shutdown();
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
                    logging.SetMinimumLevel(LogLevel.Trace);
                })
                .UseNLog();
        }
    }
}
