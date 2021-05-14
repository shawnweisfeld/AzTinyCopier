using Azure.Storage.Blobs.Models;
using System;
using System.Text;

namespace AzTinyCopier
{
    public class BlobInfo
    {
        public long Size { get; set; }
        public string ContentMD5 { get; set; }
        public DateTimeOffset LastModified { get; set; }

        public BlobInfo()
        {

        }

        public BlobInfo(BlobItemProperties blobItemProperties)
        {
            if (blobItemProperties.ContentLength.HasValue)
            {
                Size = blobItemProperties.ContentLength.Value;
            }
            else
            {
                Size = -1;
            }

            ContentMD5 = Convert.ToBase64String(blobItemProperties.ContentHash);

            if (blobItemProperties.LastModified.HasValue)
            {
                LastModified = blobItemProperties.LastModified.Value;
            }
            else
            {
                LastModified = DateTimeOffset.MinValue;
            }
        }

    }
}
