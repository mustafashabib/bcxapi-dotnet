﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Helpers;
using BCXAPI.Extensions;

namespace BCXAPI
{
    public class Service
    {
        private const string _BaseCampAPIURL = "https://basecamp.com/{0}/api/v1/{1}.json";
        private const string _AccountsURL = "https://launchpad.37signals.com/authorization.json";
        private const string _AuthorizationURL = "https://launchpad.37signals.com/authorization/new?type=web_server&client_id={0}&redirect_uri={1}{2}";
        private const string _AccessTokenURL = "https://launchpad.37signals.com/authorization/token?type=web_server&client_id={0}&redirect_uri={1}&client_secret={2}&code={3}";
        private const string _RefreshTokenURL = "https://launchpad.37signals.com/authorization/token";
        private const string _RefreshTokenPostBody = "type=refresh&client_id={0}&redirect_uri={1}&client_secret={2}&refresh_token={3}";

        private readonly string _clientID;//the client id given to you by basecamp
        private readonly string _clientSecret;//the client secret given to you by basecamp
        private readonly string _redirectURI; //this must match what you've set up in your basecamp integration page
        private readonly string _appNameAndContact; //this will go in your User-Agent header when making requests. 37s recommends you add your app name and a contact URL or email.


        private static BCXAPI.Providers.IResponseCache _cache;
        private dynamic _accessToken;
        //get or set the access token here - this way if you just got it back from basecamp you dont need to reconstruct to entire object
        public dynamic AccessToken
        {
            get
            {
                return _accessToken;
            }
            set
            {
                _accessToken = value;
            }
        }

        /// <summary>
        /// create a service class with an authorization token retrieved from GetAuthToken (if you have it). 
        /// If you do not provide one then you will only be able to get the URL to the 
        /// basecamp authorization requested page and to validate a code returned to you by that authorization.
        /// parameters come from the app you set up at integrate.37signals.com
        /// </summary>
        /// <param name="clientID">your client id from 37s</param>
        /// <param name="clientSecret">your client secret from 37s</param>
        /// <param name="redirectURI">the redirect URI you set up with 37s - this must match</param>
        /// <param name="appNameAndContact">your application name and contact info - added to your request header</param>
        /// <param name="cache">an optional cache to use for caching responses from 37s. if you don't provide one, it'll use the System.Runtime.Caching.MemoryCache.Default cache</param>
        /// <param name="accessToken">if you have an access token, provide it here. this is the entire json object returned from the call to GetAccessToken</param>
        public Service(string clientID,
            string clientSecret,
            string redirectURI,
            string appNameAndContact,
            BCXAPI.Providers.IResponseCache cache = null,
            dynamic accessToken = null)
        {
            if (cache == null)
            {
                _cache = new BCXAPI.Providers.DefaultMemoryCache();
            }
            else
            {
                _cache = cache;
            }

            _clientID = clientID;
            _clientSecret = clientSecret;
            _redirectURI = redirectURI;
            _appNameAndContact = appNameAndContact;
            _accessToken = accessToken;

            if (string.IsNullOrWhiteSpace(clientID) ||
                string.IsNullOrWhiteSpace(clientSecret) ||
                string.IsNullOrWhiteSpace(redirectURI) ||
               string.IsNullOrWhiteSpace(_appNameAndContact))
            {
                throw new Exceptions.BaseException("You must provide the client id, client secret, redirect uri, and your app name and contact information to use the API.");
            }
        }

        public bool IsAuthenticated
        {
            get
            {
                try
                {
                    return _accessToken != null && !string.IsNullOrWhiteSpace(_accessToken.access_token);

                }
                catch
                {
                    return false;
                }
            }
        }


        /// <summary>
        /// step 1: get the URL to redirect your users to
        /// </summary>
        /// <param name="optionalArguments">pass in this optional parameter to get these key value pairs passed back to your redirect URL in the query string</param>
        /// <returns>string of the URL to redirect to - since basecamp requires user authentication then you cannot make this request on the backend</returns>
        public string GetRequestAuthorizationURL(Dictionary<string, string> optionalArguments = null)
        {
            string additionalParams = string.Empty;

            if (optionalArguments != null)
            {
                System.Text.StringBuilder optionalParams = new StringBuilder();
                foreach (var kv in optionalArguments)
                {
                    optionalParams = optionalParams.AppendFormat("&{0}={1}", System.Web.HttpUtility.UrlEncode(kv.Key), System.Web.HttpUtility.UrlEncode(kv.Value));
                }
                additionalParams = optionalParams.ToString();
            }

            return string.Format(_AuthorizationURL, _clientID, _redirectURI, additionalParams);
        }

        /// <summary>
        ///step 2: Given a code that the url from GetRequestAuthorizationURL eventually redirects back to and the clientsecret you can get an access token. store this token somewhere 
        /// as you need to provide it to this wrapper to make calls.
        /// </summary>
        /// <param name="code">the code given to you by basecamp</param>
        /// <returns>the access token</returns>
        public dynamic GetAccessToken(string code)
        {
            try
            {
                string url = string.Format(_AccessTokenURL, _clientID, _redirectURI, _clientSecret, code);
                var wr = System.Net.HttpWebRequest.Create(url);
                wr.Method = "POST";
                var resp = (System.Net.HttpWebResponse)wr.GetResponse();
                using (var sw = new System.IO.StreamReader(resp.GetResponseStream()))
                {
                    _accessToken = Json.Decode(sw.ReadToEnd());
                }
                return _accessToken;
            }
            catch
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        /// <summary>
        /// helper method to make a get request to basecamp. checks cache first if you've already received that response and checks with basecamp if you
        /// need to update your cache.
        /// </summary>
        /// <param name="url">the api method endpoint being called</param>
        /// <exception cref="Exceptions.UnauthorizedException">Will be thrown if you cannot refresh the basecamp token when it has expired</exception>
        /// <exception cref="ArgumentException">URLs must end in .json</exception>
        /// <exception cref="Exceptions.RateLimitExceeded">Thrown when you exceed the ratelimit - will contain information on when you can retry</exception>
        private dynamic _getJSONFromURL(string url)
        {
            // ensure url ends with .json or .json?xxx
            if (!url.ToLower().EndsWith(".json") &&
                !(url.Contains("?") && url.ToLower().Substring(0, url.IndexOf("?")).EndsWith(".json")))
            {
                throw new ArgumentException("Invalid URL. URLs must end in .json", url);
            }

            string unique_id_to_hash = (_accessToken.access_token + url.ToLower());
            var cacheKey = unique_id_to_hash.CalculateMD5();
            try
            {
                //if in cache, check with server and if not modified then return original results
                string cached_results = (string)_cache.Get(cacheKey);
                if (cached_results != null)
                {
                    string if_none_match = (string)_cache.Get(cacheKey + "etag");
                    string if_modified_since = (string)_cache.Get(cacheKey + "lastModified");

                    System.Net.HttpWebRequest wr = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(url);
                    wr.Method = "HEAD";
                    wr.Headers.Add(System.Net.HttpRequestHeader.Authorization, string.Format("Bearer {0}", _accessToken.access_token));
                    wr.UserAgent = _appNameAndContact;
                    if (!string.IsNullOrWhiteSpace(if_modified_since))
                    {
                        wr.IfModifiedSince = DateTime.Parse(if_modified_since);
                    }
                    if (!string.IsNullOrWhiteSpace(if_none_match))
                    {
                        wr.Headers["If-None-Match"] = if_none_match;
                    }
                    var resp = (System.Net.HttpWebResponse)wr.BetterGetResponse();//use extension to properly handle 304
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                    {
                        return Json.Decode(cached_results);
                    }
                }
            }
            catch
            {
                //if cache check fails just make the real request to basecamp
            }

            try
            {
                System.Net.HttpWebRequest wr = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(url);
                wr.Method = "GET";
                wr.Headers.Add(System.Net.HttpRequestHeader.Authorization, string.Format("Bearer {0}", _accessToken.access_token));
                wr.UserAgent = _appNameAndContact;

                var resp = (System.Net.HttpWebResponse)wr.BetterGetResponse();
                if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    using (var sw = new System.IO.StreamReader(resp.GetResponseStream()))
                    {
                        var strResp = sw.ReadToEnd();
                        var json_results = Json.Decode(strResp);
                        var resp_etag = resp.Headers["ETag"] != null ? resp.Headers["ETag"] : null;
                        var resp_last_modified = resp.Headers["Last-Modified"] != null ? resp.Headers["Last-Modified"] : null;

                        if (resp_etag != null || resp_last_modified != null)
                        {
                            //cache it
                            if (!string.IsNullOrWhiteSpace(resp_etag))
                            {
                                _cache.Set(cacheKey + "etag", resp_etag);
                            }
                            if (!string.IsNullOrWhiteSpace(resp_last_modified))
                            {
                                _cache.Set(cacheKey + "lastModified", resp_last_modified);
                            }
                            if (!string.IsNullOrWhiteSpace(strResp))
                            {
                                _cache.Set(cacheKey, strResp);
                            }
                        }
                        return json_results;
                    }
                }
                else if (resp.StatusCode == (System.Net.HttpStatusCode)429)//too many requests
                {
                    throw new Exceptions.RateLimitExceededException(int.Parse(resp.Headers["Retry-After"]));
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    if (resp.Headers[System.Net.HttpResponseHeader.WwwAuthenticate] != null)
                    {
                        string www_auth = resp.Headers[System.Net.HttpResponseHeader.WwwAuthenticate];
                        int error_start = www_auth.LastIndexOf("error=\"token_expired\"");
                        if (error_start > -1)
                        {       //need to refresh token
                            throw new Exceptions.TokenExpired();
                        }
                    }

                    //throw an unauthorized exception if you get here
                    throw new Exceptions.UnauthorizedException();

                }
                else
                {
                    throw new Exceptions.GeneralAPIException("Try again later. Status code returned was " + (int)resp.StatusCode, (int)resp.StatusCode);
                }
            }
            catch (Exceptions.BaseException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private string _dynamicToJsonString(dynamic json_obj)
        {
            try
            {
                var d = json_obj as IDictionary<string, object>;
                string output = System.Web.Helpers.Json.Encode(d.ToDictionary(x => x.Key, x => x.Value));
                return output;
            }
            catch (Exception ex)
            {
                throw new Exceptions.BaseException("Cannot deserialize object to JSON string.", ex);
            }
        }

        /// <summary>
        /// helper method to make a POST request to basecamp.
        /// </summary>
        /// <param name="url">the api method endpoint being called</param>
        /// <param name="bc_item">the basecamp item being posted</param>
        /// <exception cref="Exceptions.UnauthorizedException">Will be thrown if you cannot refresh the basecamp token when it has expired</exception>
        /// <exception cref="ArgumentException">URLs must end in .json</exception>
        /// <exception cref="Exceptions.RateLimitExceeded">Thrown when you exceed the ratelimit - will contain information on when you can retry</exception>
        /// <exception cref="Exceptions.Forbidden">Thrown when you do not have access to perform the action or your account limit has been reached.</exception>
        private dynamic _postJSONToURL(string url, dynamic bc_object, out System.Uri location)
        {
            // ensure url ends with .json or .json?xxx
            if (!url.ToLower().EndsWith(".json") &&
                !(url.Contains("?") && url.ToLower().Substring(0, url.IndexOf("?")).EndsWith(".json")))
            {
                throw new ArgumentException("Invalid URL. URLs must end in .json", url);
            }

            try
            {
                System.Net.HttpWebRequest wr = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(url);
                wr.Method = "POST";
                wr.Headers.Add(System.Net.HttpRequestHeader.Authorization, string.Format("Bearer {0}", _accessToken.access_token));
                wr.UserAgent = _appNameAndContact;
                wr.ContentType = "application/json";
                string json_object = string.Empty;
                if (bc_object is string)
                {
                    json_object = (string)bc_object;
                }
                else
                {
                    json_object = _dynamicToJsonString(bc_object);
                }
                wr.ContentLength = json_object.Length;
                using (var writer = new System.IO.StreamWriter(wr.GetRequestStream()))
                {
                    writer.Write(json_object);
                }

                var resp = (System.Net.HttpWebResponse)wr.BetterGetResponse();
                if (resp.StatusCode == System.Net.HttpStatusCode.Created)
                {
                    using (var sw = new System.IO.StreamReader(resp.GetResponseStream()))
                    {
                        var strResp = sw.ReadToEnd();
                        var json_results = Json.Decode(strResp);
                        var resp_location = resp.Headers["Location"] != null ? resp.Headers["Location"] : null;
                        location = new Uri(resp_location);
                        return json_results;
                    }
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    location = null;
                    return null;
                }
                else if (resp.StatusCode == (System.Net.HttpStatusCode)429)//too many requests
                {
                    throw new Exceptions.RateLimitExceededException(int.Parse(resp.Headers["Retry-After"]));
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    if (resp.Headers[System.Net.HttpResponseHeader.WwwAuthenticate] != null)
                    {
                        string www_auth = resp.Headers[System.Net.HttpResponseHeader.WwwAuthenticate];
                        int error_start = www_auth.LastIndexOf("error=\"token_expired\"");
                        if (error_start > -1)
                        {       //need to refresh token
                            throw new Exceptions.TokenExpired();
                        }
                    }

                    //throw an unauthorized exception if you get here
                    throw new Exceptions.UnauthorizedException();

                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new Exceptions.ForbiddenException();
                }
                else
                {
                    throw new Exceptions.GeneralAPIException("Try again later. Status code returned was " + (int)resp.StatusCode, (int)resp.StatusCode);
                }
            }
            catch (Exceptions.BaseException)
            {
                throw;
            }
            catch
            {
                location = null;
                return null;
            }
        }

        /// <summary>
        /// gets the mime type for a file given its filename (with extension)
        /// </summary>
        /// <param name="file_name"></param>
        /// <returns>the mimetype via the registry</returns>
        private string _getMimeType(string file_name)
        {
            string mime = "application/octetstream";
            try
            {
                string ext = System.IO.Path.GetExtension(file_name).ToLower();
                Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(ext);
                if (rk != null && rk.GetValue("Content Type") != null)
                {
                    mime = rk.GetValue("Content Type").ToString();
                }
            }
            catch
            {
                //ignore
            }
            return mime;
        }
        /// <summary>
        /// helper method to post files to a URL, one at a time
        /// </summary>
        /// <param name="url">the api method endpoint being called</param>
        /// <param name="files">the files to upload, one at a time. Key should be a filename with extension and value should be the byte[] for the file</param>
        /// <exception cref="Exceptions.UnauthorizedException">Will be thrown if you cannot refresh the basecamp token when it has expired</exception>
        /// <exception cref="ArgumentException">URLs must end in .json</exception>
        /// <exception cref="Exceptions.RateLimitExceeded">Thrown when you exceed the ratelimit - will contain information on when you can retry</exception>
        /// <exception cref="Exceptions.Forbidden">Thrown when you do not have access to perform the action or your account limit has been reached.</exception>
        /// <returns>Dictionary<string,string> containing each file's name and it's token on the Basecamp servers</returns>
        private IDictionary<string, string> _postFilesToURL(string url, IDictionary<string, byte[]> files)
        {
            var responses = new Dictionary<string, string>();

            // ensure url ends with .json or .json?xxx
            if (!url.ToLower().EndsWith(".json") &&
                !(url.Contains("?") && url.ToLower().Substring(0, url.IndexOf("?")).EndsWith(".json")))
            {
                throw new ArgumentException("Invalid URL. URLs must end in .json", url);
            }

            try
            {
                foreach (var current_file in files)
                {
                    System.Net.HttpWebRequest wr = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(url);
                    wr.Method = "POST";
                    wr.Headers.Add(System.Net.HttpRequestHeader.Authorization, string.Format("Bearer {0}", _accessToken.access_token));
                    wr.UserAgent = _appNameAndContact;
                    string boundary = "------------------------" + DateTime.Now.Ticks.ToString("x");
                    byte[] boundary_bytes = System.Text.Encoding.ASCII.GetBytes(
                        Environment.NewLine + "--" + boundary + Environment.NewLine);

                    wr.ContentType = string.Format("{0}; boundary={1}", 
                        _getMimeType(current_file.Key), 
                        boundary);
                    wr.KeepAlive = true;

                    string formatted_data = string.Empty;
                    string file_name = current_file.Key;
                    byte[] file_bytes = current_file.Value;
                    formatted_data += boundary;
                    formatted_data += string.Format("Content-Disposition: form-data; name=\"{0}\"; filename=\"{0}\"", file_name);
                    formatted_data += Environment.NewLine +
                        string.Format("Content-Type: {0}", _getMimeType(file_name));
                    formatted_data += Environment.NewLine + Environment.NewLine;
                    formatted_data += Convert.ToBase64String(file_bytes);

                    formatted_data += Environment.NewLine + "--" + boundary + "--" + Environment.NewLine;
                    wr.ContentLength = formatted_data.Length;

                    using (var writer = new System.IO.StreamWriter(wr.GetRequestStream()))
                    {
                        writer.Write(formatted_data);
                    }

                    var resp = (System.Net.HttpWebResponse)wr.BetterGetResponse();
                    if (resp.StatusCode == System.Net.HttpStatusCode.OK || resp.StatusCode == System.Net.HttpStatusCode.NoContent)
                    {
                        using (var sw = new System.IO.StreamReader(resp.GetResponseStream()))
                        {
                            var strResp = sw.ReadToEnd();
                            dynamic json_results = Json.Decode(strResp);

                            responses.Add(file_name, json_results.token);//response from basecamp is {  "token": "4f71ea23-134660425d1818169ecfdbaa43cfc07f4e33ef4c"}
                        }
                    }
                    else if (resp.StatusCode == (System.Net.HttpStatusCode)429)//too many requests
                    {
                        throw new Exceptions.RateLimitExceededException(int.Parse(resp.Headers["Retry-After"]));
                    }
                    else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        if (resp.Headers[System.Net.HttpResponseHeader.WwwAuthenticate] != null)
                        {
                            string www_auth = resp.Headers[System.Net.HttpResponseHeader.WwwAuthenticate];
                            int error_start = www_auth.LastIndexOf("error=\"token_expired\"");
                            if (error_start > -1)
                            {       //need to refresh token
                                throw new Exceptions.TokenExpired();
                            }
                        }

                        //throw an unauthorized exception if you get here
                        throw new Exceptions.UnauthorizedException();

                    }
                    else if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        throw new Exceptions.ForbiddenException();
                    }
                    else
                    {
                        throw new Exceptions.GeneralAPIException("Try again later. Status code returned was " + (int)resp.StatusCode, (int)resp.StatusCode);
                    }
                }
                return responses;
            }
            catch (Exceptions.BaseException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// call this when you get a TokenExpired exception and store the new access token for future requests
        /// </summary>
        /// <returns>the new access token, which you should store for future calls</returns>
        public dynamic RefreshAccessToken()
        {
            try
            {
                string url = string.Format(_RefreshTokenURL);
                string post_body = string.Format(_RefreshTokenPostBody, System.Web.HttpUtility.UrlEncode(_clientID),
                    System.Web.HttpUtility.UrlEncode(_redirectURI),
                    System.Web.HttpUtility.UrlEncode(_clientSecret), System.Web.HttpUtility.UrlEncode(_accessToken.refresh_token));
                var wr = System.Net.HttpWebRequest.Create(url);
                wr.Method = "POST";
                byte[] byteArray = Encoding.UTF8.GetBytes(post_body);
                wr.ContentType = "application/x-www-form-urlencoded";
                wr.ContentLength = byteArray.Length;
                using (System.IO.Stream dataStream = wr.GetRequestStream())
                {
                    dataStream.Write(byteArray, 0, byteArray.Length);
                }

                var resp = (System.Net.HttpWebResponse)wr.GetResponse();
                using (var sw = new System.IO.StreamReader(resp.GetResponseStream()))
                {
                    _accessToken = Json.Decode(sw.ReadToEnd());
                }
                return _accessToken;
            }
            catch
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        /*gets*/
        public dynamic GetAccounts()
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(_AccountsURL);

            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetProjects(int accountID, bool archived = false)
        {
            if (IsAuthenticated)
            {
                if (archived)
                {
                    return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, "projects/archived"));
                }
                else
                {
                    return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, "projects"));
                }
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetProject(int accountID, int projectID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}", projectID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetAccessesForProject(int accountID, int projectID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/accesses", projectID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetAccessesForCalendar(int accountID, int calendarID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("calendars/{0}/accesses", calendarID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetCalendars(int accountID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, "calendars"));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetCalendar(int accountID, int calendarID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("calendars/{0}", calendarID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetPeople(int accountID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, "people"));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetPerson(int accountID, int personID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("people/{0}", personID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetTodo(int accountID, int projectID, int todoID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/todos/{1}", projectID, todoID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetDocuments(int accountID, int projectID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/documents", projectID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetDocument(int accountID, int projectID, int documentID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/documents/{1}", projectID, documentID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetTopics(int accountID, int projectID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/topics", projectID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetAttachments(int accountID, int projectID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/attachments", projectID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetUpload(int accountID, int projectID, int uploadID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/uploads/{1}", projectID, uploadID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetTodoLists(int accountID, int projectID, bool completed = false)
        {
            if (IsAuthenticated)
            {
                if (completed)
                {
                    return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/todolists/completed", projectID)));
                }
                else
                {
                    return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/todolists", projectID)));
                }
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetTodoListsWithAssignedTodos(int accountID, int personID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("people/{0}/assigned_todos", personID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetTodoList(int accountID, int projectID, int todoListID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/todolists/{1}", projectID, todoListID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetMessage(int accountID, int projectID, int messageID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/messages/{1}", projectID, messageID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetGlobalEvents(int accountID, DateTime? since = null, int page = 1)
        {
            if (IsAuthenticated)
            {
                since = since ?? DateTime.MinValue;
                string string_since = since.Value.ToString("yyyy-MM-ddTHH:mmzzz");
                string url = string.Format("{0}?since={1}",
                    string.Format(_BaseCampAPIURL, accountID, "events"),
                   string_since);
                if (page != 1)
                {
                    url = string.Format("&page={1}", url, page);
                }

                return _getJSONFromURL(url);
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetProjectEvents(int accountID, int projectID, DateTime? since = null, int page = 1)
        {
            if (IsAuthenticated)
            {
                since = since ?? DateTime.MinValue;
                string string_since = since.Value.ToString("yyyy-MM-ddTHH:mmzzz");
                string url = string.Format("{0}?since={1}",
                    string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/events", projectID)),
                   string_since);
                if (page != 1)
                {
                    url = string.Format("&page={1}", url, page);
                }

                return _getJSONFromURL(url);
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetPersonEvents(int accountID, int personID, DateTime? since = null, int page = 1)
        {
            if (IsAuthenticated)
            {
                since = since ?? DateTime.MinValue;
                string string_since = since.Value.ToString("yyyy-MM-ddTHH:mmzzz");
                string url = string.Format("{0}?since={1}",
                    string.Format(_BaseCampAPIURL, accountID, string.Format("people/{0}/events", personID)),
                   string_since);
                if (page != 1)
                {
                    url = string.Format("&page={1}", url, page);
                }

                return _getJSONFromURL(url);
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetCalendarEventsForProject(int accountID, int projectID, bool past = false)
        {
            if (IsAuthenticated)
            {
                if (!past)
                {
                    return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/calendar_events", projectID)));
                }
                else
                {
                    return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/calendar_events/past", projectID)));
                }
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetCalendarEvents(int accountID, int calendarID, bool past = false)
        {
            if (IsAuthenticated)
            {
                if (!past)
                {
                    return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("calendars/{0}/calendar_events", calendarID)));
                }
                else
                {

                    return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("calendars/{0}/calendar_events/past", calendarID)));
                }
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetCalendarEventForProject(int accountID, int projectID, int calendarEventID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/calendar_events/{1}", projectID, calendarEventID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic GetCalendarEvent(int accountID, int calendarID, int calendarEventID)
        {
            if (IsAuthenticated)
            {
                return _getJSONFromURL(string.Format(_BaseCampAPIURL, accountID, string.Format("calendars/{0}/calendar_events/{1}", calendarID, calendarEventID)));
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        /*Creation via POST*/

        /// <summary>
        /// This will create a project and return the object as it exists on basecamp. The out parameter location will give you the URL for the project.
        /// </summary>
        /// <param name="accountID">Your accountID</param>
        /// <param name="name">the name of the project</param>
        /// <param name="description">a description for the project</param>
        /// <param name="location">the out parameter whcih will contain the URL for the project once created.</param>
        /// <returns>a dynamic representing the object on basecamp's servers</returns>
        public dynamic CreateProject(int accountID, string name, string description, out Uri location)
        {
            if (IsAuthenticated)
            {
                dynamic project = new System.Dynamic.ExpandoObject();
                project.name = name;
                project.description = description;
                return _postJSONToURL(string.Format(_BaseCampAPIURL, accountID, "projects"), project, out location);
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public void GrantAccess(int accountID, int projectID, long[] ids = null, string[] email_addresses = null)
        {
            if (IsAuthenticated)
            {
                if (ids == null && email_addresses == null)
                {
                    throw new Exceptions.BaseException("Arguments missing", new ArgumentNullException("ids or email addresses"));
                }

                dynamic access = new System.Dynamic.ExpandoObject();
                access.email_addresses = email_addresses;
                access.ids = ids;
                Uri location = null;
                _postJSONToURL(string.Format(_BaseCampAPIURL, accountID, string.Format("projects/{0}/accesses", projectID)), access, out location);
                return;
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        /// <summary>
        /// Makes a request to basecamp for each file in the array - you probably want to call this method asynchronously.
        /// </summary>
        /// <param name="accountID">the account id</param>
        /// <param name="files">a key-value collection of filenames and their related file bytes</param>
        /// <returns>a key-value collection of the files uploaded and their token on basecamp. 
        /// Use this token to associate an attachment to uploads, messages, or comments.</returns>
        public IDictionary<string, string> CreateAttachments(int accountID, IDictionary<string, byte[]> files)
        {
            if (IsAuthenticated)
            {
                return _postFilesToURL(string.Format(_BaseCampAPIURL, accountID, "attachments"), files);
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }

        public dynamic CreateFileUploadForProject(int accountID, int project_id, string token, string content, string file_name, out Uri location, int[] subscribers = null)
        {
            if (IsAuthenticated)
            {
                string upload_template = "{{ " +
  "\"content\": \"{0}\"," +
  "\"attachments\": [" +
   " {{" +
   "   \"token\": \"{1}\"," +
   "   \"name\": \"{2}\"" +
   " }}" +
  "]," +
  "\"subscribers\": {3}" +
"}}";
                if (subscribers == null)
                {
                    subscribers = new int[] { };
                }
                    string subscribers_string = string.Format("[{0}]", string.Join(",", subscribers));
                    string upload = string.Format(upload_template, content, token, file_name, subscribers_string);
                
                return _postJSONToURL(
                    string.Format(_BaseCampAPIURL, accountID, 
                    string.Format("projects/{0}/uploads",project_id)), 
                    upload, out location);
            }
            else
            {
                throw new Exceptions.UnauthorizedException();
            }
        }
    }
}
