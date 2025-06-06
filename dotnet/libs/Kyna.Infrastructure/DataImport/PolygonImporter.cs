﻿using Amazon.S3;
using Amazon.S3.Model;
using Kyna.Common;
using Kyna.DataProviders.Polygon.Models;
using Kyna.Infrastructure.Database;
using Kyna.Infrastructure.Database.DataAccessObjects;
using Kyna.Infrastructure.Events;
using Kyna.Infrastructure.Logging;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kyna.Infrastructure.DataImport;

internal sealed class PolygonImporter : HttpImporterBase, IExternalDataImporter
{
    private const string BucketName = "flatfiles";

    private int? _maxParallelization = null;
    private DirectoryInfo? _downloadDirectory;
    private readonly ImportAction[] _importActions;
    private readonly ReadOnlyDictionary<string, string[]> _options;
    private readonly string _accessKey;
    private int _yearsOfData;
    private List<string> _tickers = [];
    private readonly bool _dryRun = false;

    public PolygonImporter(DbDef dbDef, DataImportConfiguration importConfig,
        Guid? processId = null, bool dryRun = false)
        : base(dbDef, Constants.Uris.Base, importConfig.ApiKey, processId)
    {
        if (!importConfig.Source.Equals(Source, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{nameof(PolygonImporter)} can only be called with an import configuration containing a source name of {Source}");
        }

        _accessKey = importConfig.AccessKey;

        _importActions = [.. ExtractImportActions(importConfig)];

        if (_importActions.Length == 0)
        {
            throw new ArgumentException("No actions specified in the import configuration.");
        }

        _options = DataImportConfiguration.CreateDictionary(importConfig.Options);

        ConfigureOptions(_options);

        _dryRun = dryRun;
    }

    public event EventHandler<CommunicationEventArgs>? Communicate;

    public const string SourceName = "polygon.io";

    public override string Source => SourceName;

    public Task<string> GetInfoAsync()
    {
        throw new NotImplementedException();
    }

    public (bool IsDangerous, string[] DangerMessages) ContainsDanger()
    {
        var purgeAction = FindImportAction(Constants.Actions.Purge);

        if (!_dryRun &&
            !purgeAction.Equals(ImportAction.Default) &&
            (purgeAction.Details!.Length == 0 ||
            !purgeAction.Details[0].Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            return (true, ["This configuration file contains a command to purge all import data and downloaded files. Are you sure you want to do this?"]);
        }

        return (false, []);
    }

    public async Task<TimeSpan> ImportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Stopwatch timer = Stopwatch.StartNew();

        await PurgeTransactionsForSourceAsync(cancellationToken).ConfigureAwait(false);

        await InvokeTickersCallAsync(cancellationToken).ConfigureAwait(false);

        await InvokeTickerDetailsCallAsync(cancellationToken).ConfigureAwait(false);

        await InvokeSplitsCallAsync(cancellationToken).ConfigureAwait(false);

        await InvokeDividendsCallAsync(cancellationToken).ConfigureAwait(false);

        await DownloadFlatFilesAsync(cancellationToken).ConfigureAwait(false);

        if (!_concurrentBag.IsEmpty)
        {
            _maxParallelization = 0;
            Communicate?.Invoke(this, new CommunicationEventArgs($"Processing {_concurrentBag.Count} stragglers.", null));

            foreach (var item in _concurrentBag)
            {
                await InvokeApiCallAsync(item.Uri, item.Category, item.SubCategory,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        timer.Stop();
        return timer.Elapsed;
    }

    private async Task DownloadFlatFilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var downloadAction = FindImportAction(Constants.Actions.FlatFiles);

        if (downloadAction.Name != null &&
            (downloadAction.Details?.Length ?? 0) > 0 &&
            _downloadDirectory != null)
        {
            CommunicateAction(Constants.Actions.FlatFiles);

            if (!_dryRun)
            {
                var fileMatchRegexes = new Regex[downloadAction.Details!.Length];

                for (int i = 0; i < downloadAction.Details!.Length; i++)
                {
                    fileMatchRegexes[i] = new Regex($@"{downloadAction.Details[i]}/\d{{4}}/\d{{2}}/([\d-]+)\.csv\.gz", RegexOptions.Singleline);
                }

                var credentials = new Amazon.Runtime.BasicAWSCredentials(_accessKey, _apiKey);
                var config = new AmazonS3Config
                {
                    ServiceURL = "https://files.polygon.io/",
                    ForcePathStyle = true
                };

                var s3Client = new AmazonS3Client(credentials, config);

                List<S3Object> s3Objects = new(500);

                try
                {
                    ListObjectsV2Request request = new()
                    {
                        BucketName = BucketName
                    };

                    ListObjectsV2Response response;
                    do
                    {
                        response = await s3Client.ListObjectsV2Async(request, cancellationToken);

                        foreach (S3Object obj in response.S3Objects)
                        {
                            // this is where the Regex file match takes place.
                            var dateOfFile = GetDateFromKey(obj.Key, fileMatchRegexes);
                            if (dateOfFile.HasValue)
                            {
                                s3Objects.Add(obj);
                            }
                        }

                        request.ContinuationToken = response.NextContinuationToken;
                    } while (response.IsTruncated.GetValueOrDefault());

                    if (s3Objects.Count > 0)
                    {
                        RemoteFile[] remoteFiles = [];

                        var sql = $@"{_dbDef.Sql.GetFormattedSqlWithWhereClause(SqlKeys.SelectRemoteFiles,
                            LogicalOperator.And, "source = @Source", "provider = @Provider")}";

                        using (var conn = _dbDef.GetConnection())
                        {
                            remoteFiles = [.. (await conn.QueryAsync<RemoteFile>(sql,
                                new { Source = SourceName, Provider = "AWS" },
                                cancellationToken: cancellationToken).ConfigureAwait(false))];
                            conn.Close();
                        }

                        foreach (var obj in s3Objects)
                        {
                            var rfMatch = remoteFiles?.FirstOrDefault(r => r.SourceName != null &&
                                r.HashCode != null &&
                                r.Size.HasValue &&
                                r.SourceName.Equals(obj.Key) &&
                                r.HashCode.Equals(obj.ETag[1..^1]) &&
                                r.Size.Equals(obj.Size));

                            if (rfMatch == null)
                            {
                                GetObjectRequest getRequest = new()
                                {
                                    BucketName = BucketName,
                                    Key = obj.Key
                                };

                                var targetFileName = FileNameHelper.ConvertSourceNameToLocalName(obj.Key);
                                if (string.IsNullOrWhiteSpace(targetFileName))
                                    throw new Exception($"Could not convert '{obj.Key}' to local file name.");

                                var targetFileFullPath = Path.Combine(_downloadDirectory.FullName, targetFileName);

                                if (File.Exists(targetFileFullPath))
                                    File.Delete(targetFileFullPath);

                                using (GetObjectResponse getResponse = await s3Client.GetObjectAsync(getRequest.BucketName,
                                    getRequest.Key, cancellationToken).ConfigureAwait(false))
                                {
                                    using Stream responseStream = getResponse.ResponseStream;
                                    using FileStream fileStream = File.Create(targetFileFullPath);
                                    await responseStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                                }

                                using var conn = _dbDef.GetConnection();
                                await conn.ExecuteAsync(_dbDef.Sql.GetSql(SqlKeys.UpsertRemoteFile),
                                    new RemoteFile()
                                    {
                                        Source = SourceName,
                                        Provider = "AWS",
                                        HashCode = obj.ETag[1..^1],
                                        Location = BucketName,
                                        SourceName = obj.Key,
                                        LocalName = targetFileName,
                                        ProcessId = _processId,
                                        Size = obj.Size,
                                        UpdateDate = DateOnly.FromDateTime(obj.LastModified.GetValueOrDefault())
                                    }, cancellationToken: cancellationToken).ConfigureAwait(false);
                                conn.Close();
                            }
                        }
                    }
                }
                catch (AmazonS3Exception e)
                {
                    Communicate?.Invoke(this, new CommunicationEventArgs(e.ToString(), nameof(PolygonImporter)));
                    KyLogger.LogError(e, nameof(PolygonImporter), _processId);
                }
                catch (Exception e)
                {
                    Communicate?.Invoke(this, new CommunicationEventArgs(e.ToString(), nameof(PolygonImporter)));
                    KyLogger.LogError(e, nameof(PolygonImporter), _processId);
                }
            }
        }
    }

    private DateOnly? GetDateFromKey(string key, Regex[] fileMatchRegexes)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        foreach (var regex in fileMatchRegexes)
        {
            var matches = regex.Matches(key);
            if (matches.Count > 0)
            {
                var dateString = matches[0].Groups[1].Value;
                if (DateOnly.TryParse(dateString, out var date))
                {
                    if (date > today.AddYears(_yearsOfData))
                    {
                        return date;
                    }
                }
            }
        }

        return null;
    }

    private async Task PurgeTransactionsForSourceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var purgeAction = FindImportAction(Constants.Actions.Purge);

        if (!purgeAction.Equals(ImportAction.Default) &&
            (purgeAction.Details!.Length == 0 ||
            !purgeAction.Details[0].Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            CommunicateAction(Constants.Actions.Purge);
            if (!_dryRun)
            {
                if (_downloadDirectory != null)
                {
                    var files = _downloadDirectory.GetFiles("*.gz").Union(
                        _downloadDirectory.GetFiles("*.csv"));

                    foreach (var file in files)
                    {
                        file.Delete();
                    }
                }

                using var conn1 = _dbDef.GetConnection();
                var t = conn1.ExecuteAsync(_dbDef.Sql.GetSql(SqlKeys.DeleteApiTransactionsForSource),
                    new { Source }, cancellationToken: cancellationToken);

                using var conn2 = _dbDef.GetConnection();
                await conn2.ExecuteAsync(_dbDef.Sql.GetSql(SqlKeys.DeleteRemoteFilesForSource),
                    new { Source }, cancellationToken: cancellationToken);

                await t;
                conn1.Close();
                conn2.Close();
            }
        }
    }

    private void CommunicateAction(string message)
    {
        message = _dryRun ? $"{message} (dry run)" : message;

        Communicate?.Invoke(this, new CommunicationEventArgs(message, nameof(PolygonImporter)));
    }

    private static IEnumerable<ImportAction> ExtractImportActions(DataImportConfiguration importConfig)
    {
        foreach (var kvp in importConfig.Actions)
        {
            var action = kvp.Key;

            if (!Constants.Actions.ValueExists(kvp.Key))
            {
                KyLogger.LogWarning($"Attempted to instantiate {nameof(PolygonImporter)} with an invalid action of {kvp.Key}.");
                continue;
            }

            string[] details = [];
            if (action == Constants.Actions.FlatFiles)
            {
                details = importConfig.ImportFilePrefixes ?? [];
            }
            else
            {
                string val = kvp.Value;
                details = string.IsNullOrWhiteSpace(val) ? []
                    : val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            }
            yield return new(action, details);
        }
    }

    private async Task InvokeTickersCallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tickerAction = FindImportAction(Constants.Actions.Tickers);

        if (tickerAction.Name != null &&
            (tickerAction.Details?.Length ?? 0) > 0 &&
            !string.IsNullOrWhiteSpace(tickerAction.Details?[0]))
        {
            CommunicateAction(Constants.Actions.Tickers);

            if (_dryRun)
                return;

            var tickerTypes = GetTickerTypes(tickerAction.Details);

            _tickers = new(150_000);
            var uri = BuildTickersUri();

            while (!string.IsNullOrWhiteSpace(uri))
            {
                var response = await GetStringResponseAsync(uri, Constants.Actions.Tickers, "US", false, cancellationToken);

                var tickerResponse = JsonSerializer.Deserialize<TickerResponse>(response, JsonSerializerOptionsRepository.Custom);

                if (tickerTypes.HasFlag(Constants.TickerTypes.Stocks))
                {
                    _tickers.AddRange(tickerResponse.Results.Where(r => !r.Code.Contains(':')).Select(r => r.Code));
                }

                if (tickerTypes.HasFlag(Constants.TickerTypes.Indexes))
                {
                    _tickers.AddRange(tickerResponse.Results.Where(r => !r.Code.StartsWith("I:")).Select(r => r.Code));
                }

                if (tickerTypes.HasFlag(Constants.TickerTypes.Currencies))
                {
                    _tickers.AddRange(tickerResponse.Results.Where(r => !r.Code.StartsWith("C:")).Select(r => r.Code));
                }

                if (tickerTypes.HasFlag(Constants.TickerTypes.Crypto))
                {
                    _tickers.AddRange(tickerResponse.Results.Where(r => !r.Code.StartsWith("X:")).Select(r => r.Code));
                }

                uri = tickerResponse.NextUrl == null ? null : $"{tickerResponse.NextUrl}&{GetToken()}";
            }
        }
    }

    private async Task InvokeTickerDetailsCallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tickerDetailsAction = FindImportAction(Constants.Actions.TickerDetails);

        if (tickerDetailsAction.Name != null &&
            (tickerDetailsAction.Details?.Length ?? 0) > 0 &&
            !tickerDetailsAction.Details![0].Equals("false", StringComparison.Ordinal))
        {
            CommunicateAction(Constants.Actions.TickerDetails);

            if (!_dryRun && _tickers.Count > 0)
            {
                foreach (var code in _tickers)
                {
                    var uri = BuildTickerDetailUri(code);
                    await InvokeApiCallAsync(uri, Constants.Actions.TickerDetails, code, false, cancellationToken);
                }
            }
        }
    }

    private async Task InvokeSplitsCallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var splitAction = FindImportAction(Constants.Actions.Splits);

        if (splitAction.Name != null &&
            (splitAction.Details?.Length ?? 0) > 0 &&
            !string.IsNullOrWhiteSpace(splitAction.Details?[0]))
        {
            CommunicateAction(Constants.Actions.Splits);

            if (!_dryRun)
            {
                var tickerTypes = GetTickerTypes(splitAction.Details);

                if (_tickers.Count > 0)
                {
                    string prefix = tickerTypes switch
                    {
                        Constants.TickerTypes t when t.HasFlag(Constants.TickerTypes.Indexes) => "I:",
                        Constants.TickerTypes t when t.HasFlag(Constants.TickerTypes.Currencies) => "C:",
                        Constants.TickerTypes t when t.HasFlag(Constants.TickerTypes.Crypto) => "X:",
                        _ => ""
                    };

                    if (string.IsNullOrEmpty(prefix))
                    {
                        if (_maxParallelization.GetValueOrDefault() > 1)
                        {
                            await Parallel.ForEachAsync(_tickers.Where(t => !t.Contains(':')), new ParallelOptions()
                            {
                                MaxDegreeOfParallelism = _maxParallelization.GetValueOrDefault()
                            }, async (code, ct) =>
                            {
                                var uri = BuildSplitUri(code);
                                await InvokeApiCallAsync(uri, Constants.Actions.Splits, code, false, cancellationToken).ConfigureAwait(false);
                            }).ConfigureAwait(false);
                        }
                        else
                        {
                            foreach (var code in _tickers.Where(t => !t.Contains(':')))
                            {
                                var uri = BuildSplitUri(code);
                                await InvokeApiCallAsync(uri, Constants.Actions.Splits, code, false, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        if (_maxParallelization.GetValueOrDefault() > 1)
                        {
                            await Parallel.ForEachAsync(_tickers.Where(t => t.StartsWith(prefix)), new ParallelOptions()
                            {
                                MaxDegreeOfParallelism = _maxParallelization.GetValueOrDefault()
                            }, async (code, ct) =>
                            {
                                var uri = BuildSplitUri(code);
                                await InvokeApiCallAsync(uri, Constants.Actions.Splits, code, false, cancellationToken).ConfigureAwait(false);
                            }).ConfigureAwait(false);
                        }
                        else
                        {
                            foreach (var code in _tickers.Where(t => t.StartsWith(prefix)))
                            {
                                var uri = BuildSplitUri(code);
                                await InvokeApiCallAsync(uri, Constants.Actions.Splits, code, false, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }
                else
                {
                    var uri = BuildSplitsUri();

                    while (!string.IsNullOrWhiteSpace(uri))
                    {
                        var response = await GetStringResponseAsync(uri, Constants.Actions.Splits, null, false, cancellationToken);

                        var splitResponse = JsonSerializer.Deserialize<SplitResponse>(response, JsonSerializerOptionsRepository.Custom);

                        uri = splitResponse.NextUrl == null ? null : $"{splitResponse.NextUrl}&{GetToken()}";
                    }
                }
            }
        }
    }

    private async Task InvokeDividendsCallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dividendAction = FindImportAction(Constants.Actions.Dividends);

        if (dividendAction.Name != null &&
            (dividendAction.Details?.Length ?? 0) > 0 &&
            !string.IsNullOrWhiteSpace(dividendAction.Details?[0]))
        {
            CommunicateAction(Constants.Actions.Dividends);

            if (!_dryRun)
            {
                var tickerTypes = GetTickerTypes(dividendAction.Details);

                if (_tickers.Count > 0)
                {
                    if (tickerTypes.HasFlag(Constants.TickerTypes.Stocks))
                    {
                        foreach (var code in _tickers.Where(t => !t.Contains(':')))
                        {
                            var uri = BuildDividendsUri(code);
                            await InvokeApiCallAsync(uri, Constants.Actions.Dividends, code, false, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    if (tickerTypes.HasFlag(Constants.TickerTypes.Indexes))
                    {
                        foreach (var code in _tickers.Where(t => t.StartsWith("I:")))
                        {
                            var uri = BuildDividendsUri(code);
                            await InvokeApiCallAsync(uri, Constants.Actions.Dividends, code, false, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    if (tickerTypes.HasFlag(Constants.TickerTypes.Currencies))
                    {
                        foreach (var code in _tickers.Where(t => t.StartsWith("C:")))
                        {
                            var uri = BuildDividendsUri(code);
                            await InvokeApiCallAsync(uri, Constants.Actions.Dividends, code, false, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    if (tickerTypes.HasFlag(Constants.TickerTypes.Crypto))
                    {
                        foreach (var code in _tickers.Where(t => t.StartsWith("X:")))
                        {
                            var uri = BuildDividendsUri(code);
                            await InvokeApiCallAsync(uri, Constants.Actions.Dividends, code, false, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    var uri = BuildDividendsUri();

                    while (!string.IsNullOrWhiteSpace(uri))
                    {
                        var response = await GetStringResponseAsync(uri, Constants.Actions.Dividends, null, false, cancellationToken);

                        var dividendResponse = JsonSerializer.Deserialize<SplitResponse>(response, JsonSerializerOptionsRepository.Custom);

                        uri = dividendResponse.NextUrl == null ? null : $"{dividendResponse.NextUrl}&{GetToken()}";
                    }
                }
            }
        }
    }

    private void ConfigureOptions(ReadOnlyDictionary<string, string[]> options)
    {
        if (options.TryGetValue(Constants.OptionKeys.MaxParallelization, out string[]? value) && value.Length != 0)
        {
            if (int.TryParse(value[0], out int maxP))
            {
                _maxParallelization = maxP;
            }
        }

        if (options.TryGetValue(Constants.OptionKeys.DownloadDirectory, out string[]? dir) && dir.Length != 0)
        {
            _downloadDirectory = new DirectoryInfo(dir[0]);
            if (!_downloadDirectory.Exists)
            {
                _downloadDirectory.Create();
            }
        }

        if (options.TryGetValue(Constants.OptionKeys.YearsOfData, out string[]? yod) && yod.Length > 0)
        {
            int y = Convert.ToInt32(yod[0]);
            _yearsOfData = Math.Abs(y) * -1;
        }
    }

    private string GetToken() => $"apikey={_apiKey}";

    private string BuildSplitsUri() =>
        $"{Constants.Uris.Base}/{Constants.Uris.Splits}?{GetToken()}";

    private string BuildDividendsUri() =>
        $"{Constants.Uris.Base}/{Constants.Uris.Dividends}?{GetToken()}";

    private string BuildSplitUri(string code) =>
        $"{Constants.Uris.Base}/{Constants.Uris.Splits}?ticker={code}&{GetToken()}";

    private string BuildDividendsUri(string code) =>
        $"{Constants.Uris.Base}/{Constants.Uris.Dividends}?ticker={code}&{GetToken()}";

    private string BuildTickersUri() =>
        $"{Constants.Uris.Base}/{Constants.Uris.Tickers}&{GetToken()}";

    private string BuildTickerDetailUri(string code) =>
        $"{Constants.Uris.Base}/{Constants.Uris.TickerDetails}/{code}?{GetToken()}";

    private ImportAction FindImportAction(string actionName) =>
        _importActions.FirstOrDefault(a => a.Name != null &&
            a.Name.Equals(actionName, StringComparison.OrdinalIgnoreCase));

    readonly string[] _stockTypes = ["s", "stock", "stocks"];
    readonly string[] _indexTypes = ["i", "index", "indexes"];
    readonly string[] _currencyTypes = ["c", "currency", "currencies"];
    readonly string[] _cryptoTypes = ["x", "crypto", "cryptos"];

    private Constants.TickerTypes GetTickerTypes(string[] tickersText)
    {
        var text = tickersText.Select(t => t.Trim().ToLower()).ToArray();

        var result = Constants.TickerTypes.None;

        if (_stockTypes.Intersect(text).Any())
            result |= Constants.TickerTypes.Stocks;

        if (_indexTypes.Intersect(text).Any())
            result |= Constants.TickerTypes.Indexes;

        if (_currencyTypes.Intersect(text).Any())
            result |= Constants.TickerTypes.Currencies;

        if (_cryptoTypes.Intersect(text).Any())
            result |= Constants.TickerTypes.Crypto;

        return result;
    }

    public static class Constants
    {
        [Flags]
        public enum TickerTypes
        {
            None = 0,
            Stocks = 1 << 0,
            Indexes = 1 << 1,
            Currencies = 1 << 2,
            Crypto = 1 << 3
        }

        public static class Actions
        {
            public const string FlatFiles = "Flat Files";
            public const string Tickers = "Tickers";
            public const string TickerDetails = "Ticker Details";
            public const string Purge = "Purge";
            public const string Splits = "Splits";
            public const string Dividends = "Dividends";

            private static readonly FieldInfo[] _fields = typeof(Actions).GetFields(BindingFlags.Public | BindingFlags.Static);

            public static bool ValueExists(string? text, bool caseSensitive = false)
            {
                if (text == null)
                {
                    return false;
                }

                foreach (var field in _fields)
                {
                    if ((caseSensitive && text.Equals(field.GetValue(null)?.ToString())) ||
                        (!caseSensitive && text.Equals(field.GetValue(null)?.ToString(), StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public static class Uris
        {
            public const string Base = "https://api.polygon.io";
            public const string Splits = "v3/reference/splits";
            public const string Tickers = "v3/reference/tickers?active=true";
            public const string TickerDetails = "v3/reference/tickers";
            public const string Dividends = "v3/reference/dividends";
        }

        public static class OptionKeys
        {
            public const string MaxParallelization = "Max Parallelization";
            public const string DownloadDirectory = "Import File Location";
            public const string ImportFilePrefixes = "Import File Prefixes";
            public const string YearsOfData = "Years of Data";
        }
    }

    public class ImportConfigfile(IDictionary<string, string> importActions,
        IDictionary<string, string>? exchanges,
        IDictionary<string, string>? symbolTypes,
        string[]? importFilePrefixes,
        IDictionary<string, string>? options,
        IDictionary<string, string>? dateRanges)
    {
        [JsonPropertyName("Import Actions")]
        public IDictionary<string, string> ImportActions { get; set; } = importActions;

        public IDictionary<string, string>? Exchanges { get; set; } = exchanges;

        [JsonPropertyName("Symbol Types")]
        public IDictionary<string, string>? SymbolTypes { get; set; } = symbolTypes;
        [JsonPropertyName("Import File Prefixes")]
        public string[]? ImportFilePrefixes { get; set; } = importFilePrefixes;
        public IDictionary<string, string>? Options { get; set; } = options;

        [JsonPropertyName("Date Ranges")]
        public IDictionary<string, string>? DateRanges { get; set; } = dateRanges;
    }

    public class DataImportConfiguration(string source,
        string apiKey,
        string accessKey,
        IDictionary<string, string>? importActions,
        string[]? importFilePrefixes,
        IDictionary<string, string>? options)
    {
        public string ApiKey { get; } = apiKey;
        public string AccessKey { get; } = accessKey;
        public string Source { get; } = source;
        public string[]? ImportFilePrefixes { get; } = importFilePrefixes;
        public IDictionary<string, string> Actions { get; } = importActions ?? new Dictionary<string, string>();
        public IDictionary<string, string>? Options { get; } = options;
        internal static ReadOnlyDictionary<string, string[]> CreateDictionary(IDictionary<string, string>? dict)
        {
            var result = new Dictionary<string, string[]>(dict?.Keys.Count ?? 0);

            if (dict != null)
            {
                foreach (var kvp in dict)
                {
                    string val = kvp.Value;
                    string[] vals = string.IsNullOrWhiteSpace(val)
                        ? []
                        : val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                    result.Add(kvp.Key.Trim(), vals);
                }
            }

            return new ReadOnlyDictionary<string, string[]>(result);
        }
    }
}

file static class FileNameHelper
{
    public static string ConvertSourceNameToLocalName(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            return sourceName.Trim();

        var splitName = sourceName.Split('/');

        if (splitName.Length < 3)
            return sourceName;

        return $"{splitName[0]}_{splitName[1]}_{splitName.Last()}";
    }
}