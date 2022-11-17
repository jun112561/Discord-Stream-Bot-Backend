﻿using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Discord_Stream_Bot_Backend.Controllers
{
    [Route("[action]")]
    [ApiController]
    public class YouTubeMemberController : ControllerBase
    {
        private readonly ILogger<YouTubeMemberController> _logger; 
        private readonly HttpClient _httpClient;
        private readonly GoogleAuthorizationCodeFlow flow;

        public YouTubeMemberController(ILogger<YouTubeMemberController> logger, HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
            flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = Utility.ServerConfig.GoogleClientId,
                    ClientSecret = Utility.ServerConfig.GoogleClientSecret
                },
                Scopes = new string[] { "https://www.googleapis.com/auth/youtube.force-ssl" },
                DataStore = new RedisDataStore(RedisConnection.Instance.ConnectionMultiplexer)
            });
        }

        [EnableCors("allowGET")]
        [HttpGet]
        public async Task<APIResult> DiscordCallBack(string code)
        {
            if (string.IsNullOrEmpty(code))
                return new APIResult(ResultStatusCode.BadRequest, "參數錯誤");

            try
            {
                if (await Utility.RedisDb.KeyExistsAsync($"discord:code:{code}"))
                    return new APIResult(ResultStatusCode.BadRequest, "請確認是否有插件或軟體導致重複驗證\n如網頁正常顯示資料則無需理會");

                await Utility.RedisDb.StringSetAsync($"discord:code:{code}", "0", TimeSpan.FromHours(1));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.ToString(), "DiscordCallBack - Redis設定錯誤");
                return new APIResult(ResultStatusCode.InternalServerError, "伺服器內部錯誤，請向孤之界回報");
            }

            try
            {
                TokenData tokenData = null;
                try
                {
                    var content = new FormUrlEncodedContent(new KeyValuePair<string, string>[]
                    {
                        new("code", code),
                        new("client_id", Utility.ServerConfig.DiscordClientId),
                        new("client_secret", Utility.ServerConfig.DiscordClientSecret),
                        new("redirect_uri", Utility.ServerConfig.RedirectURI),
                        new("grant_type", "authorization_code")
                    });
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                    var response = await _httpClient.PostAsync("https://discord.com/api/v10/oauth2/token", content);
                    
                    response.EnsureSuccessStatusCode();

                    tokenData = JsonConvert.DeserializeObject<TokenData>(await response.Content.ReadAsStringAsync());

                    if (tokenData == null || tokenData.access_token == null)
                        return new APIResult(ResultStatusCode.Unauthorized, "認證錯誤，請重新登入Discord");
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("400"))
                    {
                        _logger.LogWarning(ex.ToString());
                        return new APIResult(ResultStatusCode.BadRequest, "請重新登入Discord");
                    }

                    _logger.LogError(ex.ToString(), "DiscordCallBack - Discord Token交換錯誤");
                    return new APIResult(ResultStatusCode.InternalServerError, "伺服器內部錯誤，請向孤之界回報");
                }

                DiscordUser discordUser = null;
                try
                {
                    _httpClient.DefaultRequestHeaders.Clear();                    
                    //_httpClient.DefaultRequestHeaders.Add("Accept", "application/json");                    
                    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue($"Bearer", tokenData.access_token);

                    var discordMeJson = await _httpClient.GetStringAsync("https://discord.com/api/v10/users/@me");
                    discordUser = JsonConvert.DeserializeObject<DiscordUser>(discordMeJson);

                    _logger.LogInformation($"Discord User OAuth Done: {discordUser.username} ({discordUser.id})");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.ToString(), "DiscordCallBack - Discord API回傳錯誤");
                    return new APIResult(ResultStatusCode.InternalServerError, "伺服器內部錯誤，請向孤之界回報");
                }

                string token = "";
                try
                {
                    token = Auth.TokenManager.CreateToken(discordUser.id);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex.ToString(), "DiscordCallBack - 建立JWT錯誤");
                    return new APIResult(ResultStatusCode.InternalServerError, "伺服器內部錯誤，請向孤之界回報");
                }

                return new APIResult(ResultStatusCode.OK, new { Token = token, DiscordData = discordUser });
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex.ToString(), "DiscordCallBack - 整體錯誤");
                return new APIResult(ResultStatusCode.InternalServerError, "伺服器內部錯誤，請向孤之界回報");
            }

        }

        [EnableCors("allowGET")]
        [HttpGet]
        public async Task<APIResult> GoogleCallBack(string code, string state)
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return new APIResult(ResultStatusCode.BadRequest, "參數錯誤");

            try
            {
                if (await Utility.RedisDb.KeyExistsAsync($"google:code:{code}"))
                    return new APIResult(ResultStatusCode.BadRequest, "請確認是否有插件或軟體導致重複驗證\n如網頁正常顯示資料則無需理會");

                await Utility.RedisDb.StringSetAsync($"google:code:{code}", "0", TimeSpan.FromHours(1));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.ToString(), "GoogleCallBack - Redis設定錯誤");
                return new APIResult(ResultStatusCode.InternalServerError, "伺服器內部錯誤，請向孤之界回報");
            }

            try
            {
                var discordUser = Auth.TokenManager.GetUser<string>(state);
                if (string.IsNullOrEmpty(discordUser))
                    return new APIResult(ResultStatusCode.Unauthorized, "Token無效，請重新登入Discord");

                var googleUser = await flow.LoadTokenAsync(discordUser, CancellationToken.None);

                var googleToken = await flow.ExchangeCodeForTokenAsync(discordUser, code, Utility.ServerConfig.RedirectURI, CancellationToken.None);
                if (googleToken == null)
                    return new APIResult(ResultStatusCode.Unauthorized, "請重新登入Google帳號");

                if (string.IsNullOrEmpty(googleToken.AccessToken))
                    return new APIResult(ResultStatusCode.Unauthorized, "Google授權驗證無效\n請解除應用程式授權後再登入Google帳號");

                if (!string.IsNullOrEmpty(googleToken.RefreshToken))
                {
                    return new APIResult(ResultStatusCode.OK);
                }
                else if (googleUser != null && !string.IsNullOrEmpty(googleUser.RefreshToken))
                {
                    googleToken.RefreshToken = googleUser.RefreshToken;
                    await flow.DataStore.StoreAsync(discordUser, googleToken);
                    return new APIResult(ResultStatusCode.OK);
                }
                else
                {
                    return await RevokeGoogleToken(discordUser, "無法刷新Google授權\n請重新登入Google帳號", ResultStatusCode.Unauthorized);
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("invalid_grant"))
                {
                    _logger.LogWarning("偵測到invalid_grant");
                    return new APIResult(ResultStatusCode.Unauthorized, "Google授權驗證無效或尚未登入Google\n請解除應用程式授權後再登入Google帳號");
                }
                else
                {
                    _logger.LogError(ex.ToString(), "GoogleCallBack - 整體錯誤");
                    return new APIResult(ResultStatusCode.InternalServerError, "伺服器內部錯誤，請向孤之界回報");
                }
            }
        }

        [EnableCors("allowGET")]
        [HttpGet]
        public async Task<APIResult> GetGoogleData(string token = "")
        {
            if (string.IsNullOrEmpty(token))
                return new APIResult(ResultStatusCode.BadRequest, "Token不可為空");

            try
            {
                var discordUser = Auth.TokenManager.GetUser<string>(token);
                if (string.IsNullOrEmpty(discordUser))
                    return new APIResult(ResultStatusCode.Unauthorized, "Token無效");

                var googleToken = await flow.LoadTokenAsync(discordUser, CancellationToken.None);
                if (googleToken == null)
                    return new APIResult(ResultStatusCode.Unauthorized, "請登入Google帳號");

                if (string.IsNullOrEmpty(googleToken.AccessToken))
                    return new APIResult(ResultStatusCode.Unauthorized, "Google授權驗證無效\n請解除應用程式授權後再登入Google帳號");

                if (string.IsNullOrEmpty(googleToken.RefreshToken))
                    return await RevokeGoogleToken(discordUser, "無法刷新Google授權\n請重新登入Google帳號", ResultStatusCode.Unauthorized);

                try
                {
                    if (googleToken.IssuedUtc.AddSeconds((double)googleToken.ExpiresInSeconds).Subtract(DateTime.UtcNow).TotalSeconds <= 0)
                        googleToken = await flow.RefreshTokenAsync(discordUser, googleToken.RefreshToken, CancellationToken.None);                    
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.ToString(), "GetGoogleData - 刷新 Token 錯誤");
                    return await RevokeGoogleToken(discordUser, "無法刷新Google授權\n請重新登入Google帳號", ResultStatusCode.Unauthorized);
                }

                MemberData user = new MemberData();
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", googleToken.AccessToken);

                try
                {
                    var youtubeUserData = JsonConvert.DeserializeObject<YoutubeChannelMeJson>(
                        await _httpClient.GetStringAsync($"https://youtube.googleapis.com/youtube/v3/channels?part=id%2Csnippet&mine=true"))
                            .items.First();

                    user.YoutubeChannelId = youtubeUserData.id;
                    user.GoogleUserName = youtubeUserData.snippet.title;
                    user.GoogleUserAvatar = youtubeUserData.snippet.thumbnails.@default.url;
                }
                catch (ArgumentNullException)
                {
                    return await RevokeGoogleToken(discordUser, "請確認此Google帳號有Youtube頻道", ResultStatusCode.BadRequest);
                }
                catch (WebException ex)
                {
                    if (ex.Message.Contains("403"))
                    {
                        _logger.LogError(ex.ToString(), $"403錯誤");
                        return await RevokeGoogleToken(discordUser, "請重新登入，並在登入Google帳號時勾選\n\"查看、編輯及永久刪除您的 YouTube 影片、評價、留言和字幕\"", ResultStatusCode.Unauthorized);
                    }
                    else if (ex.Message.Contains("401"))
                    {
                        _logger.LogError(ex.ToString(), $"401錯誤");
                        return new APIResult(ResultStatusCode.InternalServerError, "請嘗試重新登入Google帳號");
                    }
                    else
                    {
                        _logger.LogError(ex.ToString(), "GetGoogleData - Youtube API回傳錯誤");
                        return new APIResult(ResultStatusCode.InternalServerError, "伺服器內部錯誤，請向孤之界回報");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.ToString(), "GetGoogleData - 其他錯誤");
                    return new APIResult(ResultStatusCode.InternalServerError, "伺服器內部錯誤，請向孤之界回報");
                }

                _logger.LogInformation($"Google User OAuth Fetch Done: {user.GoogleUserName}");
                return new APIResult(ResultStatusCode.OK, new { UserName = user.GoogleUserName, UserAvatarUrl = user.GoogleUserAvatar });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.ToString(), "GetGoogleData - 整體錯誤");
                return new APIResult(ResultStatusCode.InternalServerError, "伺服器內部錯誤，請向孤之界回報");
            }
        }

        [EnableCors("allowGET")]
        [HttpGet]
        public async Task<APIResult> UnlinkGoogle(string token = "")
        {
            if (string.IsNullOrEmpty(token))
                return new APIResult(ResultStatusCode.BadRequest, "Token不可為空");

            var discordUser = Auth.TokenManager.GetUser<string>(token);
            if (string.IsNullOrEmpty(discordUser))
                return new APIResult(ResultStatusCode.Unauthorized, "Token無效，請重新登入Discord");

            return await RevokeGoogleToken(discordUser);
        }

        private async Task<APIResult> RevokeGoogleToken(string discordUser = "", string resultText = null, ResultStatusCode resultStatusCode = ResultStatusCode.OK)
        {
            if (string.IsNullOrEmpty(discordUser))
                return new APIResult(ResultStatusCode.BadRequest, "參數錯誤");

            try
            {
                await Utility.RedisSub.PublishAsync("member.revokeToken", discordUser);

                var googleToken = await flow.LoadTokenAsync(discordUser, CancellationToken.None);
                if (googleToken == null)
                    return new APIResult(ResultStatusCode.Unauthorized, "未登入Google帳號或已解除綁定");

                if (string.IsNullOrEmpty(googleToken.AccessToken))
                    return new APIResult(ResultStatusCode.Unauthorized, "Google授權驗證無效\n請手動至Google帳號安全性解除應用程式授權");

                string revokeToken = googleToken.RefreshToken ?? googleToken.AccessToken;

                await flow.RevokeTokenAsync(discordUser, revokeToken, CancellationToken.None);

                return new APIResult(resultStatusCode, resultText);
            }
            catch (Exception ex)
            {
                await flow.DeleteTokenAsync(discordUser, CancellationToken.None);
                _logger.LogError(ex.ToString(), "RevokeGoogleToken - 整體錯誤");
                return new APIResult(ResultStatusCode.InternalServerError, "伺服器內部錯誤，請向孤之界回報\n請手動至Google帳號安全性解除應用程式授權");
            }
        }
    }
}
