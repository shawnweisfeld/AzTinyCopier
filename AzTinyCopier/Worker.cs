using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Azure.Storage.Sas;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AzTinyCopier
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IHostApplicationLifetime _hostApplicationLifetime;
        private TelemetryClient _telemetryClient;
        private Config _config;

        public Worker(ILogger<Worker> logger,
            IHostApplicationLifetime hostApplicationLifetime,
            TelemetryClient telemetryClient,
            Config config)
        {
            _logger = logger;
            _hostApplicationLifetime = hostApplicationLifetime;
            _telemetryClient = telemetryClient;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            stoppingToken.Register(() =>
            {
                _logger.LogInformation("Worker Cancelling");
            });

            try
            {
                while (true)
                {
                    if (!await ProcessQueueMessage(stoppingToken))
                    {
                        await Task.Delay(TimeSpan.FromMinutes(_config.SleepWait));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operation Canceled");
            }
            catch (Exception ex)
            {
                _telemetryClient.TrackException(ex);
                _logger.LogError(ex, "Unhandled Exception");
            }
            finally
            {
                _logger.LogInformation("Flushing App Insights");
                _telemetryClient.Flush();
                Task.Delay(5000).Wait();

                _hostApplicationLifetime.StopApplication();
            }

        }

        protected async Task<bool> ProcessQueueMessage(CancellationToken cancellationToken)
        {
            var queueClient = new QueueClient(_config.OperationConnection, _config.QueueName);
            QueueMessage queueMessage = null;
            Message msg = null;

            using (var op = _telemetryClient.StartOperation<DependencyTelemetry>("GetMessage"))
            {

                op.Telemetry.Properties.Add("Run", _config.Run);
                op.Telemetry.Properties.Add("WhatIf", _config.WhatIf.ToString());
                op.Telemetry.Properties.Add("OperationConnection", queueClient.AccountName);
                op.Telemetry.Properties.Add("QueueName", _config.QueueName);
                op.Telemetry.Properties.Add("VisibilityTimeout", _config.VisibilityTimeout.ToString());

                await queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

                if (queueClient.Exists())
                {
                    queueMessage = await queueClient.ReceiveMessageAsync(TimeSpan.FromMinutes(_config.VisibilityTimeout), cancellationToken);

                    if (queueMessage != null)
                    {
                        msg = Message.FromString(queueMessage.MessageText);
                    }
                }

                op.Telemetry.Properties.Add("QueueEmpty", (queueMessage == null).ToString());
            }

            if (msg == null)
            {
                return false;
            }


            if (msg.Action.Equals("ProcessAccount", StringComparison.InvariantCultureIgnoreCase))
            {
                _logger.LogInformation("ProcessAccount");
                //Create one queue message for each container in the source account
                using (var op = _telemetryClient.StartOperation<DependencyTelemetry>("ProcessAccount"))
                {
                    var sourceBlobServiceClient = new BlobServiceClient(_config.SourceConnection);
                    int containerCount = 0;

                    foreach (var container in sourceBlobServiceClient.GetBlobContainers())
                    {
                        await queueClient.SendMessageAsync((new Message()
                        {
                            Action = "ProcessPath",
                            Container = container.Name,
                            Path = string.Empty
                        }).ToString());
                        containerCount++;
                    }

                    op.Telemetry.Properties.Add("Run", _config.Run);
                    op.Telemetry.Properties.Add("WhatIf", _config.WhatIf.ToString());
                    op.Telemetry.Properties.Add("SourceConnection", sourceBlobServiceClient.AccountName);
                    op.Telemetry.Properties.Add("containerCount", containerCount.ToString());
                }
            }
            else if (msg.Action.Equals("ProcessPath", StringComparison.InvariantCultureIgnoreCase))
            {
                _logger.LogInformation($"ProcessPath: {msg.Container} {msg.Path}");
                using (var op = _telemetryClient.StartOperation<DependencyTelemetry>("ProcessPath"))
                {
                    var sourceBlobServiceClient = new BlobServiceClient(_config.SourceConnection);
                    var sourceBlobContainerClient = sourceBlobServiceClient.GetBlobContainerClient(msg.Container);
                    var sasBuilder = new BlobSasBuilder()
                    {
                        BlobContainerName = msg.Container,
                        Resource = "c",
                        ExpiresOn = DateTimeOffset.UtcNow.AddHours(_config.SleepWait)
                    };
                    sasBuilder.SetPermissions(BlobAccountSasPermissions.Read);
                    Uri sasUri = sourceBlobContainerClient.GenerateSasUri(sasBuilder);
                    var sourceBlobs = new ConcurrentDictionary<string, long>();

                    var destinationBlobServiceClient = new BlobServiceClient(_config.DestinationConnection);
                    var destinationBlobContainerClient = destinationBlobServiceClient.GetBlobContainerClient(msg.Container);
                    var destinationBlobs = new ConcurrentDictionary<string, long>();
                    await destinationBlobContainerClient.CreateIfNotExistsAsync();

                    var operationBlobServiceClient = new BlobServiceClient(_config.OperationConnection);
                    var operationBlobContainerClient = operationBlobServiceClient.GetBlobContainerClient(msg.Container);
                    await operationBlobContainerClient.CreateIfNotExistsAsync();


                    long blobCount = 0;
                    long blobBytes = 0;
                    long blobCountMoved = 0;
                    long blobBytesMoved = 0;
                    long subPrefixes = 0;

                    if (string.IsNullOrEmpty(_config.Delimiter))
                    {
                        _config.Delimiter = "/";
                    }

                    if (_config.ThreadCount < 1)
                    {
                        _config.ThreadCount = Environment.ProcessorCount * 8;
                    }
                    var slim = new SemaphoreSlim(_config.ThreadCount);

                    var getSourceTask = Task.Run(async () =>
                    {
                        await foreach (var item in sourceBlobContainerClient.GetBlobsByHierarchyAsync(prefix: msg.Path, delimiter: _config.Delimiter, cancellationToken: cancellationToken))
                        {
                            if (item.IsPrefix)
                            {
                                await queueClient.SendMessageAsync((new Message()
                                {
                                    Action = "ProcessPath",
                                    Container = msg.Container,
                                    Path = item.Prefix
                                }).ToString());
                                subPrefixes++;
                            }
                            else if (item.IsBlob)
                            {
                                sourceBlobs.TryAdd(item.Blob.Name, item.Blob.Properties.ContentLength.GetValueOrDefault());
                            }
                        }
                    });

                    var getDestinationTask = Task.Run(async () =>
                    {
                        await foreach (var item in destinationBlobContainerClient.GetBlobsByHierarchyAsync(prefix: msg.Path, delimiter: _config.Delimiter, cancellationToken: cancellationToken))
                        {
                            if (item.IsBlob)
                            {
                                destinationBlobs.TryAdd(item.Blob.Name, item.Blob.Properties.ContentLength.GetValueOrDefault());
                            }
                        }
                    });

                    await Task.WhenAll(getSourceTask, getDestinationTask);

                    var blobs = sourceBlobs.ToDictionary(x => x.Key, x => new
                    {
                        SourceBytes = x.Value,
                        DestinationBytes = destinationBlobs.ContainsKey(x.Key) ? destinationBlobs[x.Key] : -1
                    });

                    await File.WriteAllLinesAsync("status.csv", blobs.Select(x => $"{x.Key}, {x.Value.SourceBytes}, {x.Value.DestinationBytes}"));
                    var toUpload = operationBlobContainerClient.GetBlobClient($"{msg.Path}status.csv");
                    await toUpload.DeleteIfExistsAsync(cancellationToken: cancellationToken);
                    await toUpload.UploadAsync("status.csv", cancellationToken: cancellationToken);

                    var blobSet = new ConcurrentBag<Task>();

                    foreach (var blob in blobs)
                    {
                        blobSet.Add(Task.Run(async () =>
                        {
                            await slim.WaitAsync(cancellationToken);

                            if (blob.Value.DestinationBytes == -1)
                            {
                                if (!_config.WhatIf)
                                {
                                    var dest = destinationBlobContainerClient.GetBlobClient(blob.Key);
                                    var source = sourceBlobContainerClient.GetBlobClient(blob.Key);

                                    await dest.SyncCopyFromUriAsync(new Uri($"{source.Uri.AbsoluteUri}{sasUri.Query}"));
                                }

                                Interlocked.Add(ref blobCountMoved, 1);
                                Interlocked.Add(ref blobBytesMoved, blob.Value.SourceBytes);
                            }

                            Interlocked.Add(ref blobCount, 1);
                            Interlocked.Add(ref blobBytes, blob.Value.SourceBytes);

                            slim.Release();
                        }));
                    }

                    await Task.WhenAll(blobSet.ToArray());


                    op.Telemetry.Properties.Add("Run", _config.Run);
                    op.Telemetry.Properties.Add("WhatIf", _config.WhatIf.ToString());
                    op.Telemetry.Properties.Add("ThreadCount", _config.ThreadCount.ToString());
                    op.Telemetry.Properties.Add("Container", msg.Container);
                    op.Telemetry.Properties.Add("SourceConnection", sourceBlobServiceClient.AccountName);
                    op.Telemetry.Properties.Add("DestinationConnection", destinationBlobServiceClient.AccountName);
                    op.Telemetry.Properties.Add("Delimiter", _config.Delimiter);
                    op.Telemetry.Properties.Add("Prefix", msg.Path);
                    op.Telemetry.Properties.Add("blobCount", blobCount.ToString());
                    op.Telemetry.Properties.Add("blobBytes", blobBytes.ToString());
                    op.Telemetry.Properties.Add("blobCountMoved", blobCountMoved.ToString());
                    op.Telemetry.Properties.Add("blobBytesMoved", blobBytesMoved.ToString());
                    op.Telemetry.Properties.Add("subPrefixes", subPrefixes.ToString());
                }
            }

            using (var op = _telemetryClient.StartOperation<DependencyTelemetry>("Remove Queue Message"))
            {

                op.Telemetry.Properties.Add("Run", _config.Run);
                op.Telemetry.Properties.Add("WhatIf", _config.WhatIf.ToString());
                op.Telemetry.Properties.Add("OperationConnection", queueClient.AccountName);
                op.Telemetry.Properties.Add("QueueName", _config.QueueName);

                await queueClient.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt, cancellationToken: cancellationToken);
            }

            return true;
        }

    }
}
