using System;
using System.Collections.ObjectModel;

namespace Project_Jumpstart_Undertale_Mod_Manager.Models;

public enum LogLevel { Info, Warn, Error }

public sealed record LogEntry(LogLevel Level, DateTime Time, string Text);

// ObservableCollection that trims from the front once it passes a cap.
public sealed class RingLog(int max = 500) : ObservableCollection<LogEntry>
{
    public void Append(LogEntry e)
    {
        Add(e);
        while (Count > max) RemoveAt(0);   // on UI thread already (see SendLog)
    }
}