using System.Collections.Generic;
using System.IO;
using Godot;

namespace OnlyBebop;

/// audio sheet: plays cached Deadlock sounds by audio row id; systems.music loops Only Up!'s music.
public partial class Sounds : Node
{
    readonly Dictionary<string, AudioStream> _streams = new();

    public static AudioStream? LoadStream(string path)
    {
        if (!File.Exists(path)) return null;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" => AudioStreamWav.LoadFromFile(path),
            ".ogg" => AudioStreamOggVorbis.LoadFromFile(path),
            ".mp3" => AudioStreamMP3.LoadFromFile(path),
            _ => null,
        };
    }

    public void Load(System.Text.Json.Nodes.JsonObject? deadlock)
    {
        if (deadlock?["sounds"] is not System.Text.Json.Nodes.JsonObject s) return;
        foreach (var (id, rel) in s)
            if (LoadStream(ContentCache.Abs("deadlock", rel!.GetValue<string>())) is { } st) _streams[id] = st;
    }

    public void Play(string id)
    {
        if (!_streams.TryGetValue(id, out var st)) return;
        var p = new AudioStreamPlayer { Stream = st, VolumeDb = -4 };
        AddChild(p);
        p.Finished += p.QueueFree;
        p.Play();
    }
}

public partial class MusicPlayer : AudioStreamPlayer
{
    public bool Start(System.Text.Json.Nodes.JsonObject? onlyup)
    {
        if (onlyup?["music"] is not System.Text.Json.Nodes.JsonArray tracks || tracks.Count == 0) return false;
        var st = Sounds.LoadStream(ContentCache.Abs("onlyup", tracks[0]!["file"]!.GetValue<string>()));
        if (st is null) return false;
        switch (st)
        {
            case AudioStreamWav w: w.LoopMode = AudioStreamWav.LoopModeEnum.Forward; w.LoopEnd = (int)(w.GetLength() * w.MixRate); break;
            case AudioStreamOggVorbis o: o.Loop = true; break;
            case AudioStreamMP3 m: m.Loop = true; break;
        }
        Stream = st; VolumeDb = -8; Play();
        return true;
    }
}
