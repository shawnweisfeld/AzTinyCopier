namespace AzTinyCopier
{
    public class Config
    {
        public string Run { get; set; }
        public bool WhatIf { get; set; }
        public string SourceConnection { get; set; }
        public string DestinationConnection { get; set; }
        public string OperationConnection { get; set; }
        public string QueueName { get; set; }

        public int VisibilityTimeout { get; set; } // in minutes
        public int SleepWait { get; set; } // in minutes
        public string Delimiter { get; set; }

        public int ThreadCount { get; set; }
    }
}