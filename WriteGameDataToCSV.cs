using System;
using System.IO;

namespace PoPTracker
{
    public static class WriteGameDataToCSV
    {
        public static void InitializeCSV(string filepath1, string filepath2)
        {
            if (!File.Exists(filepath1))
            {
                WriteToCSV(filepath1, "Location,itemID,Acquisition");
            }
            if (!File.Exists(filepath2))
            {
                WriteToCSV(filepath2, "itemID");
            }
        }

        public static void WriteToCSV(string filepath, string data)
        {
            using(StreamWriter sw = new StreamWriter(filepath, true))
            {
                sw.WriteLine(data);
            }
        }
        
    }
}