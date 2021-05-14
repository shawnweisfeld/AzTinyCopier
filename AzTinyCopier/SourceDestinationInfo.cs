using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AzTinyCopier
{
    public class SourceDestinationInfo
    {
        public BlobInfo Source { get; set; }
        public BlobInfo Destination { get; set; }

        public SourceDestinationInfo(BlobInfo source = null, BlobInfo destination = null)
        {
            Source = source;
            Destination = destination;
        }

    }
}
