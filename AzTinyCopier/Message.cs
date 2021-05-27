using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AzTinyCopier
{
    public class Message
    {
        public string Action { get; set; }
        public string Container { get; set; }
        public string Path { get; set; }

        public static Message FromString(string msg)
        {
            msg = msg.Trim();

            //if it doesnt start with a { assume it is base64 encoded. 
            if (!msg.StartsWith("{"))
            {
                var bytes = Convert.FromBase64String(msg);
                msg = Encoding.UTF8.GetString(bytes);
            }

            return JsonConvert.DeserializeObject<Message>(msg);
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }

    }
}
