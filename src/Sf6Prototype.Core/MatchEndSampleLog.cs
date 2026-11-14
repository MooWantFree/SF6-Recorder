using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Sf6Prototype;

// Used only by the detection worker. Append is memory-only; batches are flushed
// every two seconds, on a confirmed end, or on shutdown. Keep four bounded files.
public sealed class MatchEndSampleLog : IDisposable
{
    private static readonly UTF8Encoding Encoding=new(false);
    private readonly List<string> _pending=[];
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private readonly int _maxFileBytes,_backupFiles,_batchRows;
    private readonly double _flushSeconds;
    private readonly string _session=Guid.NewGuid().ToString("N");
    private StreamWriter? _writer;
    private long _fileBytes,_sequence;
    private TimeSpan _nextFlush;
    public string FilePath {get;}
    public long WrittenEntries {get;private set;}
    public long DroppedEntries {get;private set;}
    public long FlushCount {get;private set;}
    public string? LastError {get;private set;}
    public MatchEndSampleLog(string directory,int maxFileBytes=4*1024*1024,int backupFiles=3,int batchRows=64,double flushSeconds=2)
    {
        if(maxFileBytes<512 || backupFiles is <1 or >10 || batchRows is <1 or >1024 || !double.IsFinite(flushSeconds) || flushSeconds<=0)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        FilePath=Path.Combine(Path.GetFullPath(directory),"match-detection-samples.jsonl");
        _maxFileBytes=maxFileBytes;_backupFiles=backupFiles;_batchRows=batchRows;_flushSeconds=flushSeconds;
        _nextFlush=TimeSpan.FromSeconds(flushSeconds);
    }
    public void Append(DateTimeOffset at,long? sample,MatchFrameObservation? observation,bool armedBefore,bool armedAfter,
        bool ended,string phase,double frequency,double? sampleMilliseconds,string refresh)
    {
        var result=observation is not { } frame ? phase : ended ? "match_end" :
            frame.BattleHud && frame.ResultScore<0.6 ? "battle_hud" : !armedAfter ? "not_armed" :
            frame.ResultScore>=MatchEndDetection.ResultThreshold ? "confirming_end" : "no_result";
        var row=new {at=at.ToLocalTime(),session=_session,sequence=++_sequence,sample,
            score=observation is { } value ? (double?)Math.Round(value.ResultScore,4) : null,
            recognizer=MatchEndDetection.Version,pattern=observation?.ResultPattern,
            threshold=MatchEndDetection.ResultThreshold,strongThreshold=MatchEndDetection.StrongResultThreshold,battleHud=observation?.BattleHud,
            armedBefore,armed=armedAfter,result,phase,samplesPerSecond=frequency,
            sampleMilliseconds=sampleMilliseconds is { } ms ? (double?)Math.Round(ms,3) : null,refresh};
        _pending.Add(JsonSerializer.Serialize(row));
        if(_pending.Count>=_batchRows) Flush();
    }
    public void FlushIfDue()
    {if(_pending.Count>0 && _clock.Elapsed>=_nextFlush) Flush();}
    public void AppendFailure(MatchEndFailure failure,string? image,string? imageError)
    {
        var row=new {at=failure.At.ToLocalTime(),recordedAt=DateTimeOffset.Now,session=_session,sequence=++_sequence,
            sample=failure.Sample,score=Math.Round(failure.Observation.ResultScore,4),threshold=MatchEndDetection.ResultThreshold,
            recognizer=MatchEndDetection.Version,pattern=failure.Observation.ResultPattern,
            armed=failure.Armed,result="unconfirmed_result",reason=failure.Reason,samplesPerSecond=failure.Frequency,image,imageError};
        _pending.Add(JsonSerializer.Serialize(row));
        if(_pending.Count>=_batchRows) Flush();
    }
    public void Flush()
    {
        _nextFlush=_clock.Elapsed+TimeSpan.FromSeconds(_flushSeconds);
        if(_pending.Count==0) return;
        var written=0;
        try
        {
            EnsureWriter();
            foreach(var row in _pending)
            {
                var bytes=Encoding.GetByteCount(row)+1;
                if(_fileBytes>0 && _fileBytes+bytes>_maxFileBytes) Rotate();
                _writer!.WriteLine(row);_fileBytes+=bytes;written++;
            }
            _writer!.Flush();WrittenEntries+=written;FlushCount++;LastError=null;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never interrupt detection or accumulate an unbounded backlog.
            LastError=ex.GetType().Name;DroppedEntries+=_pending.Count;
            CloseWriter();
        }
        finally {_pending.Clear();CloseWriter();}
    }
    private void EnsureWriter()
    {
        if(_writer!=null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var stream=new FileStream(FilePath,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete,16*1024);
        _fileBytes=stream.Length;
        _writer=new StreamWriter(stream,Encoding,8*1024) {NewLine="\n"};
    }
    private void Rotate()
    {
        _writer!.Flush();_writer.Dispose();_writer=null;
        for(var index=_backupFiles;index>=1;index--)
        {
            var from=index==1 ? FilePath : Archive(index-1);
            if(File.Exists(from)) File.Move(from,Archive(index),overwrite:true);
        }
        EnsureWriter();
    }
    private string Archive(int index)=>Path.Combine(Path.GetDirectoryName(FilePath)!, $"match-detection-samples.{index}.jsonl");
    private void CloseWriter()
    {
        try {_writer?.Dispose();}
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        finally {_writer=null;}
    }
    public void Dispose() {Flush();CloseWriter();}
}
