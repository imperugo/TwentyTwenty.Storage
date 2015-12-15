﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Google;
using Google.Apis.Storage.v1;
using Google.Apis.Upload;
using Google.Apis.Services;
using Google.Apis.Auth.OAuth2;
using Blob = Google.Apis.Storage.v1.Data.Object;
using PredefinedAcl = Google.Apis.Storage.v1.ObjectsResource.InsertMediaUpload.PredefinedAclEnum;
using Google.Apis.Requests;
using System.Security.Cryptography.X509Certificates;

namespace TwentyTwenty.Storage.Google
{
    public class GoogleStorageProvider : IStorageProvider
    {
        private const string BlobNameRegex = @"(?<Container>[^/]+)/(?<Blob>.+)";

        private const string DefaultContentType = "application/octet-stream";

        readonly private StorageService _storageService;

        readonly private string _bucket;

        readonly private string _serviceEmail;

        readonly private string _certificatePath;

        public GoogleStorageProvider(GoogleProviderOptions options)
        {
            _serviceEmail = options.Email;

            _certificatePath = options.CertificatePath;

            // TODO: Throw error that private key required
            // TODO: Need to handle exceptions for invalid secerets
            if (options.PrivateKey != null)
            {
                var credential =
                    new ServiceAccountCredential(new ServiceAccountCredential.Initializer(options.Email)
                    {
                        Scopes = new[] { StorageService.Scope.DevstorageFullControl }
                    }.FromCertificate(new X509Certificate2(_certificatePath, "notasecret", X509KeyStorageFlags.Exportable)));

                _storageService = new StorageService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential
                });
            }
            else
            {
                _storageService = new StorageService();
            }

            _bucket = options.Bucket;
        }

        public void SaveBlobStream(string containerName, string blobName, Stream source, BlobProperties properties = null)
        {
            try
            {
                var response = SaveRequest(containerName, blobName, source, properties).Upload();

                //Google's errors are all generic, so there's really no way that I currently know to detect what went wrong exactly.
                if (response.Status == UploadStatus.Failed)
                {
                    throw Error(response.Exception as GoogleApiException, message: "There was an error uploading to Google Cloud Storage");
                }
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public async Task SaveBlobStreamAsync(string containerName, string blobName, Stream source, BlobProperties properties = null)
        {
            try
            {
                var response = await SaveRequest(containerName, blobName, source, properties).UploadAsync();

                if (response.Status == UploadStatus.Failed)
                {
                    throw Error(response.Exception as GoogleApiException, message: "There was an error uploading to Google Cloud Storage");
                }
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public Stream GetBlobStream(string containerName, string blobName)
        {
            try
            {
                return AsyncHelpers.RunSync(() => _storageService.HttpClient.GetStreamAsync(GetBlob(containerName, blobName).MediaLink));
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public async Task<Stream> GetBlobStreamAsync(string containerName, string blobName)
        {
            try
            {
                return await _storageService.HttpClient.GetStreamAsync(GetBlob(containerName, blobName).MediaLink);
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public string GetBlobUrl(string containerName, string blobName)
        {
            try
            {
                return GetBlob(containerName, blobName).MediaLink;
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public string GetBlobSasUrl(string containerName, string blobName, DateTimeOffset expiry, bool isDownload = false,
            string fileName = null, string contentType = null, BlobUrlAccess access = BlobUrlAccess.Read)
        {
            return new GoogleSignedUrlGenerator(_certificatePath, _serviceEmail, _bucket)
                .GetSignedUrl($"{containerName}/{blobName}", expiry, contentType, fileName);
        }

        public BlobDescriptor GetBlobDescriptor(string containerName, string blobName)
        {
            try
            {
                return GetBlobDescriptor(GetBlob(containerName, blobName));
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public async Task<BlobDescriptor> GetBlobDescriptorAsync(string containerName, string blobName)
        {
            try
            {
                return GetBlobDescriptor(await GetBlobAsync(containerName, blobName));
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public IList<BlobDescriptor> ListBlobs(string containerName)
        {
            try
            {
                return GetListBlobsRequest(containerName).Execute().Items.SelectToListOrEmpty(GetBlobDescriptor);
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public async Task<IList<BlobDescriptor>> ListBlobsAsync(string containerName)
        {
            try
            {
                return (await GetListBlobsRequest(containerName).ExecuteAsync()).Items.SelectToListOrEmpty(GetBlobDescriptor);
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public void DeleteBlob(string containerName, string blobName)
        {
            try
            {
                _storageService.Objects.Delete(_bucket, $"{containerName}/{blobName}").Execute();
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public async Task DeleteBlobAsync(string containerName, string blobName)
        {
            try
            {
                await _storageService.Objects.Delete(_bucket, $"{containerName}/{blobName}").ExecuteAsync();
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public void DeleteContainer(string containerName)
        {
            try
            {
                var batch = new BatchRequest(_storageService);

                foreach (var blob in ListBlobs(containerName))
                {
                    batch.Queue<string>(_storageService.Objects.Delete(_bucket, $"{blob.Container}/{blob.Name}"), 
                        (content, error, i, message) => { });
                }

                AsyncHelpers.RunSync(() => batch.ExecuteAsync());
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public async Task DeleteContainerAsync(string containerName)
        {
            try
            {
                var batch = new BatchRequest(_storageService);

                foreach (var blob in await ListBlobsAsync(containerName))
                {
                    batch.Queue<string>(_storageService.Objects.Delete(_bucket, $"{blob.Container}/{blob.Name}"),
                        (content, error, i, message) => { });
                }

                await batch.ExecuteAsync();
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public void UpdateBlobProperties(string containerName, string blobName, BlobProperties properties)
        {
            try
            {
                UpdateRequest(containerName, blobName, properties).Execute();
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        public async Task UpdateBlobPropertiesAsync(string containerName, string blobName, BlobProperties properties)
        {
            try
            {
                await UpdateRequest(containerName, blobName, properties).ExecuteAsync();
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        #region Helpers

        private ObjectsResource.InsertMediaUpload SaveRequest(string containerName, string blobName, Stream source, BlobProperties properties)
        {
            var blob = CreateBlob(containerName, blobName, properties);

            var req = _storageService.Objects.Insert(blob, _bucket, source,
                properties?.ContentType ?? DefaultContentType);

            req.PredefinedAcl = properties?.Security == BlobSecurity.Public ? PredefinedAcl.PublicRead : PredefinedAcl.Private__;
            
            return req;
        }

        private ObjectsResource.UpdateRequest UpdateRequest(string containerName, string blobName, BlobProperties properties)
        {
            var blob = CreateBlob(containerName, blobName, properties);
            var req = _storageService.Objects.Update(blob, _bucket, $"{containerName}/{blobName}");
            req.PredefinedAcl = properties?.Security == BlobSecurity.Public ? ObjectsResource.UpdateRequest.PredefinedAclEnum.PublicRead : ObjectsResource.UpdateRequest.PredefinedAclEnum.Private__;
            return req;
        }

        private Blob CreateBlob(string containerName, string blobName, BlobProperties properties = null)
        {
            return new Blob
            {
                Name = $"{containerName}/{blobName}",
                ContentType = properties?.ContentType ?? DefaultContentType
            };
        }

        private Task<Blob> GetBlobAsync(string containerName, string blobName, DateTimeOffset? endEx = null, bool isDownload = false, string optionalFileName = null)
        {
            //TODO:  Use the optional fields
            //TODO:  Verify that unless the optional fields are provided, the URL provided will NOT be SAS.
            var req = _storageService.Objects.Get(_bucket, $"{containerName}/{blobName}");
            try
            {
                return req.ExecuteAsync();
            }
            catch (GoogleApiException e)
            {
                if (e.Message.Contains("404"))
                {
                    return null;
                }

                throw;
            }
        }

        private Blob GetBlob(string containerName, string blobName, DateTimeOffset? endEx = null,
            bool isDownload = false, string optionalFileName = null)
        {
            //TODO:  Use the optional fields
            //TODO:  Verify that unless the optional fields are provided, the URL provided will NOT be SAS.
            var req = _storageService.Objects.Get(_bucket, $"{containerName}/{blobName}");
            req.Projection = ObjectsResource.GetRequest.ProjectionEnum.Full;

            try
            {
                return req.Execute();
            }
            catch (GoogleApiException gae)
            {
                throw Error(gae);
            }
        }

        private BlobDescriptor GetBlobDescriptor(Blob blob)
        {
            var match = Regex.Match(blob.Name, BlobNameRegex);
            if (!match.Success)
            {
                throw new InvalidOperationException("Unable to match blob name with regex; all blob names");
            }

            var blobDescriptor = new BlobDescriptor
            {
                Container = match.Groups["Container"].Value,
                ContentMD5 = blob.Md5Hash,
                ContentType = blob.ContentType,
                ETag = blob.ETag,
                LastModified = DateTimeOffset.Parse(blob.UpdatedRaw),
                Length = Convert.ToInt64(blob.Size),
                Name = match.Groups["Blob"].Value,
                Security = blob.Acl != null 
                    && blob.Acl.Any(acl => acl.Entity.ToLowerInvariant() == "allusers") ? BlobSecurity.Public : BlobSecurity.Private,
                Url = blob.MediaLink
            };

            return blobDescriptor;
        }

        private ObjectsResource.ListRequest GetListBlobsRequest(string containerName)
        {
            var req = _storageService.Objects.List(_bucket);
            req.Prefix = containerName;
            return req;
        }

        private StorageException Error(GoogleApiException gae, int code = 1001, string message = null)
        {
            return new StorageException(new StorageError()
            {
                Code = code,
                Message =
                    message ?? "Encountered an error when making a request to Google's Cloud API.  Unfortunately, as of the time this is being developed, Google's error messages (when using the .NET client library) are not very informative, and do not usually provide any clues as to what may have gone wrong.",
                ProviderMessage = gae?.Message
            }, gae);
        }

        #endregion
    }
}