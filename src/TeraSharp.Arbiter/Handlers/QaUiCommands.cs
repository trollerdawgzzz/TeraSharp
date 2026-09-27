// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text;
using Microsoft.Extensions.Logging;
using TeraSharp.Arbiter.Network;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Handlers;

/// <summary>T201: native client QA packets; these commands do not open a server-side browser.</summary>
public static class QaUiCommands
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(new[] {
        "cc", "clientCommand", "change_deco_ui", "change_voice", "init_awesomium", "observer_mode",
        "open_awesomium", "set_awesomium_debug_mode", "set_awesomium_web_url", "string", "versionInfoHide",
        "ps_com_window", "ps_vote_window", "ps_guard_window", "ps_reg_window",
    }, StringComparer.OrdinalIgnoreCase);

    public static bool TryExecute(GameSession s, CharacterStore? store, GmCommandLine line, ILogger log)
    {
        if (!Names.Contains(line.Name)) return false;
        int id = (int)(s.SelectedCharacter?.Id ?? s.PlayerId);
        switch (line.Name.ToLowerInvariant())
        {
            case "ps_com_window": s.Send(Packet(0x7CF5, Array.Empty<byte>())); break; // Arb044:5672 -> Arb074:2745.
            case "ps_vote_window": s.Send(Packet(0x730B, Array.Empty<byte>())); break; // Arb044:5704 -> Arb074:2834.
            case "ps_guard_window": s.Send(Packet(0x8D78, Array.Empty<byte>())); break; // Arb044:5565.
            case "ps_reg_window": // Arb074:2610–2741: current Classic runtime's election state is0, never1.
                s.Send(StringPacket(0xF30E, "@1391")); break;
            case "init_awesomium": s.Send(Packet(0xF5E5, Array.Empty<byte>())); break; // Arb043:15059.
            case "open_awesomium": // Arb043:19218 -> Account::AdminOpenAwesomium, Arb061:2459.
            case "set_awesomium_web_url": // Arb044:2807.
                if (line.Args.Count == 1) s.Send(StringPacket(line.Name.Equals("open_awesomium", StringComparison.OrdinalIgnoreCase)
                    ? (ushort)0x7D57 : (ushort)0xC558, line.Arg(0)));
                break;
            case "set_awesomium_debug_mode": // Arb044:2692: every token except literal lowercase off selects true.
                if (line.Args.Count == 1) s.Send(Packet(0xA1AE, new[] { line.Arg(0) == "off" ? (byte)0 : (byte)1 }));
                break;
            case "versioninfohide": // Arb044:8081: string-ref11, int0, boolfalse, empty string.
                s.Send(Packet(0xB767, new byte[] { 11, 0, 0, 0, 0, 0, 0, 0, 0 })); break;
            case "string": // Arb044:5790; PE DAT_140d3dfe8 is ':'.
                if (line.Args.Count == 2) s.Send(StringPacket(0x5C97, "@" + line.Arg(0) + ":" + line.Arg(1), 4));
                break;
            case "cc":
            case "clientcommand": // Arb044:2173-2454: first argument is a command, not a target user.
                if (line.Args.Count > 0)
                {
                    s.Send(BuildClientCommand(line.Args));
                    GmCommandHandlers.SendCustom(s, "clientCommand = " + string.Join(" ", line.Args) + " ");
                }
                break;
            case "change_voice": // Arb040:7967 -> Arb028:7537: replace customization bits8..15, persist.
                if (line.Args.Count > 0)
                {
                    int voice = QaGeneralCommands.NativeInt(line.Arg(0));
                    store?.SetQaVoice(id, unchecked((byte)voice));
                    if (s.SelectedCharacter?.Appearance is { Length: >= 2 } appearance) appearance[1] = unchecked((byte)voice);
                    s.Send(Packet(0x5CBB, BitConverter.GetBytes(voice)));
                }
                break;
            case "observer_mode": // Arb044:4306 -> Arb028:1303: state/SQL only, no packet.
                if (line.Args.Count == 1 && line.Arg(0).Equals("on", StringComparison.OrdinalIgnoreCase)) store?.SetQaObserverType(id, 3);
                else if (line.Args.Count == 1 && line.Arg(0).Equals("off", StringComparison.OrdinalIgnoreCase)) store?.SetQaObserverType(id, -1);
                break;
            case "change_deco_ui": // Arb040:7470 -> Arb070:15193; contents type33, selected id, true.
                if (line.Args.Count == 1 && store != null)
                {
                    int deco = QaGeneralCommands.NativeInt(line.Arg(0)); store.SetQaDecoUi(deco);
                    if (Program.World is { } world)
                        for (int w = 0; w < WorldRegistration.MaxWorldId; w++)
                            if (world.HasLinks(w)) world.SendFrame(w, 0x1588, BuildDecoContents(deco));
                }
                break;
        }
        return true;
    }

    public static byte[] BuildDecoContents(int id)
    {
        var p = new byte[9]; BitConverter.GetBytes(33).CopyTo(p, 0); BitConverter.GetBytes(id).CopyTo(p, 4); p[8] = 1; return p;
    }

    /// <summary>Arb070:11510 SendDecoUI: on lobby initialization only; no immediate push on change.</summary>
    public static byte[] BuildDecoUi(CharacterStore? store) => Packet(0x57B3, BitConverter.GetBytes(store?.GetQaDecoUi() ?? 0));

    public static byte[] BuildClientCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw new ArgumentException("A native client command needs its name.", nameof(args));
        int length = 10 + Encoding.Unicode.GetByteCount(args[0]) + 2;
        foreach (string arg in args.Skip(1)) length = checked(length + 6 + Encoding.Unicode.GetByteCount(arg) + 2);
        if (length > ushort.MaxValue) throw new ArgumentException("Client command exceeds the native packet size.", nameof(args));
        var packet = Packet(0xC6D9, new byte[length - 4]);
        BitConverter.GetBytes((ushort)(args.Count - 1)).CopyTo(packet, 4); packet[8] = 10;
        int cursor = 10; Encoding.Unicode.GetBytes(args[0] + "\0").CopyTo(packet, cursor); cursor += Encoding.Unicode.GetByteCount(args[0]) + 2;
        int previous = -1;
        foreach (string arg in args.Skip(1))
        {
            BitConverter.GetBytes((ushort)cursor).CopyTo(packet, previous < 0 ? 6 : previous + 2);
            BitConverter.GetBytes((ushort)cursor).CopyTo(packet, cursor);
            BitConverter.GetBytes((ushort)(cursor + 6)).CopyTo(packet, cursor + 4);
            previous = cursor; cursor += 6;
            Encoding.Unicode.GetBytes(arg + "\0").CopyTo(packet, cursor); cursor += Encoding.Unicode.GetByteCount(arg) + 2;
        }
        return packet;
    }

    internal static byte[] StringPacket(ushort opcode, string text, int zeroPrefix = 0)
    {
        byte[] value = Encoding.Unicode.GetBytes(text + "\0");
        var payload = new byte[2 + zeroPrefix + value.Length];
        BitConverter.GetBytes((ushort)(6 + zeroPrefix)).CopyTo(payload, 0); value.CopyTo(payload, 2 + zeroPrefix);
        return Packet(opcode, payload);
    }

    private static byte[] Packet(ushort opcode, byte[] payload)
    {
        var p = new byte[4 + payload.Length]; BitConverter.GetBytes(checked((ushort)p.Length)).CopyTo(p, 0);
        BitConverter.GetBytes(opcode).CopyTo(p, 2); payload.CopyTo(p, 4); return p;
    }
}
