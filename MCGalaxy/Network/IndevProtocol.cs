﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using MCGalaxy.Events.PlayerEvents;
using MCGalaxy.Events.ServerEvents;
using System.Text;
using BlockID = System.UInt16;

namespace MCGalaxy.Network
{
    public class IndevProtocol : GameSession, INetProtocol
    {
        const int OPCODE_PING      = 0x00;
        const int OPCODE_LOGIN     = 0x01;
        const int OPCODE_HANDSHAKE = 0x02;
        const int OPCODE_CHAT      = 0x03;
        const int OPCODE_SPAWN_POSITION = 0x06;

        const int OPCODE_SELF_STATEONLY = 0x0A;
        const int OPCODE_SELF_MOVE      = 0x0B;
        const int OPCODE_SELF_LOOK      = 0x0C;
        const int OPCODE_SELF_MOVE_LOOK = 0x0D;
        const int OPCODE_BLOCK_DIG      = 0x0E;
        const int OPCODE_BLOCK_PLACE    = 0x0F;

        const int OPCODE_ARM_ANIM  = 0x12;
        const int OPCODE_NAMED_ADD = 0x14;
        const int OPCODE_REMOVE_ENTITY = 0x1D;
        const int OPCODE_REL_MOVE  = 0x1F;
        const int OPCODE_LOOK      = 0x20;
        const int OPCODE_REL_MOVE_LOOK = 0x21;
        const int OPCODE_TELEPORT  = 0x22;
        const int OPCODE_PRE_CHUNK = 0x32;
        const int OPCODE_CHUNK     = 0x33;
        const int OPCODE_BLOCK_CHANGE = 0x35;

        const int PROTOCOL_VERSION = 9;

        // NOTE indev replaces bottom 2 layers with lava
        //  although second layer *can* be replaced via SetBlock,
        //  bottom client will always be hardcoded to lava
        // so have to shift the whole world up instead
        const int WORLD_SHIFT_BLOCKS = 2;
        const int WORLD_SHIFT_COORDS = 64;

        public IndevProtocol(INetSocket s) {
            socket = s;
            player = new Player(s, this);
        }

        protected override int HandlePacket(byte[] buffer, int offset, int left) {
            //Console.WriteLine("IN: " + buffer[offset]);
            switch (buffer[offset]) {
                case OPCODE_PING:      return 1; // Ping
                case OPCODE_LOGIN:     return HandleLogin(buffer, offset, left);
                case OPCODE_HANDSHAKE: return HandleHandshake(buffer, offset, left);
                case OPCODE_CHAT:      return HandleChat(buffer, offset, left);
                case OPCODE_SELF_STATEONLY: return HandleSelfStateOnly(buffer, offset, left);
                case OPCODE_SELF_MOVE:      return HandleSelfMove(buffer, offset, left);
                case OPCODE_SELF_LOOK:      return HandleSelfLook(buffer, offset, left);
                case OPCODE_SELF_MOVE_LOOK: return HandleSelfMoveLook(buffer, offset, left);
                case OPCODE_BLOCK_DIG:      return HandleBlockDig(buffer, offset, left);
                case OPCODE_BLOCK_PLACE:    return HandleBlockPlace(buffer, offset, left);
                case OPCODE_ARM_ANIM:       return HandleArmAnim(buffer, offset, left);

                default:
                    player.Leave("Unhandled opcode \"" + buffer[offset] + "\"!", true);
                    return -1;
            }
        }

        static int ReadStringLength(byte[] buffer, int offset) {
            return ReadU16(buffer, offset) * 2;
        }

        static string ReadString(byte[] buffer, int offset) {
            int len = ReadStringLength(buffer, offset);
            return Encoding.BigEndianUnicode.GetString(buffer, offset + 2, len);
        }

        static int CalcStringLength(string value) {
            return Encoding.BigEndianUnicode.GetByteCount(value);
        }

        static void WriteString(byte[] buffer, int offset, string value) {
            int len = Encoding.BigEndianUnicode.GetBytes(value, 0, value.Length, buffer, offset + 2);
            WriteU16((ushort)(len / 2), buffer, offset);
        }

        static ushort ReadU16(byte[] array, int index) {
            return NetUtils.ReadU16(array, index);
        }

        static void WriteU16(ushort value, byte[] array, int index) {
            NetUtils.WriteU16(value, array, index);
        }

        static int ReadI32(byte[] array, int index) {
            return NetUtils.ReadI32(array, index);
        }

        static void WriteI32(int value, byte[] array, int index) {
            NetUtils.WriteI32(value, array, index);
        }

        unsafe static float ReadF32(byte[] array, int offset) {
            int value = ReadI32(array, offset);
            return *(float*)&value;
        }

        unsafe static void WriteF32(float value, byte[] buffer, int offset) {
            int num = *(int*)&value;
            WriteI32(num, buffer, offset + 0);
        }

        BlockID ReadBlock(byte[] buffer, int offset) { return Block.FromRaw(buffer[offset]); }

        public override void Disconnect() { player.Disconnect(); }


#region Classic processing
        int HandleLogin(byte[] buffer, int offset, int left) {
            int size = 1 + 4; // opcode + version;
            int strLen;
            if (left < size) return 0;

            int version = ReadI32(buffer, offset + 1);
            if (version != PROTOCOL_VERSION) {
                player.Leave("Unsupported protocol version!"); return -1;
            }

            if (left < size + 2)          return 0;
            strLen = ReadStringLength(buffer, offset + size);
            if (left < size + 2 + strLen) return 0;
            string name = ReadString(buffer,  offset + size);
            size += 2 + strLen;

            // TODO what do these 8 bytes even do? usually 0
            if (left < size + 8) return 0;
            size += 8;

            // TODO I dunno what these two strings are really for

            if (left < size + 2) return 0;
            strLen = ReadStringLength(buffer, offset + size);
            if (left < size + 2 + strLen) return 0;
            string motd1 = ReadString(buffer, offset + size); // usually "Loading level..."
            size += 2 + strLen;

            if (left < size + 2) return 0;
            strLen = ReadStringLength(buffer, offset + size);
            if (left < size + 2 + strLen) return 0;
            string motd2 = ReadString(buffer, offset + size); // usually "Loading server..."
            size += 2 + strLen;

            Logger.Log(LogType.SystemActivity, "MOTD 1: " + motd1);
            Logger.Log(LogType.SystemActivity, "MOTd 2:" + motd2);
            if (!player.ProcessLogin(name, "")) return -1;

            ProtocolVersion = Server.VERSION_0030;
            for (byte b = 0; b < Block.CPE_COUNT; b++)
            {
                fallback[b] = Block.ConvertLimited(b, this);
            }
            ProtocolVersion = 0;

            player.CompleteLoginProcess();
            return size;
        }

        int HandleHandshake(byte[] buffer, int offset, int left) {
            int size = 1 + 2; // opcode + name length
            if (left < size) return 0;
            
            // enough data for name length?
            size += ReadStringLength(buffer, offset + 1);
            if (left < size) return 0;
            string name = ReadString(buffer, offset + 1);

            // TEMP HACK
            player.name = name; player.truename = name;
            Logger.Log(LogType.SystemActivity, "REA!: " + name);

            // TODO what even is this string
            SendHandshake("-");

            return size;
        }

        int HandleChat(byte[] buffer, int offset, int left) {
            int size = 1 + 2; // opcode + text length
            if (left < size) return 0;
            
            // enough data for name length?
            size += ReadStringLength(buffer, offset + 1);
            if (left < size) return 0;
            string text = ReadString(buffer, offset + 1);

            player.ProcessChat(text, false);
            return size;
        }

        int HandleSelfStateOnly(byte[] buffer, int offset, int left) {
            int size = 1 + 1;
            if (left < size) return 0;
            // bool state

            Position pos    = player.Pos;
            Orientation rot = player.Rot;
            player.ProcessMovement(pos.X, pos.Y, pos.Z, rot.RotY, rot.HeadX, 0);
            return size;
        }

        int HandleSelfMove(byte[] buffer, int offset, int left) {
            int size = 1 + 4 + 4 + 4 + 4 + 1;
            if (left < size) return 0;

            float x = ReadF32(buffer, offset + 1);
            float y = ReadF32(buffer, offset + 5);
            float s = ReadF32(buffer, offset + 9);
            float z = ReadF32(buffer, offset + 13);
            // bool state

            y += 1.59375f; // feet -> 'head' position
            y -= WORLD_SHIFT_BLOCKS;

            Orientation rot = player.Rot;
            player.ProcessMovement((int)(x * 32), (int)(y * 32), (int)(z * 32),
                              rot.RotY, rot.HeadX, 0);
            return size;
        }

        int HandleSelfLook(byte[] buffer, int offset, int left) {
            int size = 1 + 4 + 4 + 1;
            if (left < size) return 0;

            float yaw   = ReadF32(buffer, offset + 1) + 180.0f;
            float pitch = ReadF32(buffer, offset + 5);
            // bool state

            Position pos = player.Pos;
            player.ProcessMovement(pos.X, pos.Y, pos.Z,
                              (byte)(yaw / 360.0f * 256.0f), (byte)(pitch / 360.0f * 256.0f), 0);
            return size;
        }

        int HandleSelfMoveLook(byte[] buffer, int offset, int left) {
            int size = 1 + 4 + 4 + 4 + 4 + 4 + 4 + 1;
            if (left < size) return 0;

            float x = ReadF32(buffer, offset + 1);
            float y = ReadF32(buffer, offset + 5);
            float s = ReadF32(buffer, offset + 9);
            float z = ReadF32(buffer, offset + 13);

            float yaw   = ReadF32(buffer, offset + 17) + 180.0f;
            float pitch = ReadF32(buffer, offset + 21);
            // bool state

            y += 1.59375f; // feet -> 'head' position
            y -= WORLD_SHIFT_BLOCKS;

            player.ProcessMovement((int)(x * 32), (int)(y * 32), (int)(z * 32),
                              (byte)(yaw / 360.0f * 256.0f), (byte)(pitch / 360.0f * 256.0f), 0);
            return size;
        }

        int HandleBlockDig(byte[] buffer, int offset, int left) {
            int size = 1 + 1 + 4 + 1 + 4 + 1;
            if (left < size) return 0;

            byte status = buffer[offset + 1];
            int x    = ReadI32(buffer, offset + 2);
            int y    = buffer[offset + 6];
            int z    = ReadI32(buffer, offset + 7);
            byte dir = buffer[offset + 11];
            y -= WORLD_SHIFT_BLOCKS;

            if (status == 2)
                player.ProcessBlockchange((ushort)x, (ushort)y, (ushort)z, 0, 0);
            return size;
        }

        int HandleBlockPlace(byte[] buffer, int offset, int left) {
            int size = 1 + 2 + 4 + 1 + 4 + 1;
            if (left < size) return 0;

            BlockID block = ReadU16(buffer, offset + 1);
            int x    = ReadI32(buffer, offset + 3);
            int y    = buffer[offset + 7];
            int z    = ReadI32(buffer, offset + 8);
            byte dir = buffer[offset + 12];
            y -= WORLD_SHIFT_BLOCKS;

            player.ProcessBlockchange((ushort)x, (ushort)y, (ushort)z, 1, block);
            return size;
        }

        int HandleArmAnim(byte[] buffer, int offset, int left) {
            int size = 1 + 4 + 1;
            if (left < size) return 0;

            // TODO something
            return size;
        }
        #endregion


        void SendHandshake(string serverID) {
            Send(MakeHandshake(serverID));
        }

        public override void SendTeleport(byte id, Position pos, Orientation rot) {
            if (id == Entities.SelfID) {
                Send(MakeSelfMoveLook(pos, rot));
            } else {
                Send(MakeEntityTeleport(id, pos, rot));
            }
        }

        public override void SendRemoveEntity(byte id) {
            byte[] data = new byte[1 + 4];
            data[0] = OPCODE_REMOVE_ENTITY;
            WriteI32(id, data, 1);
            Send(data);
        }

        public override void SendChat(string message) {
            message = CleanupColors(message);
            List<string> lines = LineWrapper.Wordwrap(message, true);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Replace('&', '§');
                Send(MakeChat(line));
            }
        }

        public override void SendMessage(CpeMessageType type, string message) {
            message = CleanupColors(message);
            if (type != CpeMessageType.Normal) return;
            message = message.Replace('&', '§');
            Send(MakeChat(message));
        }

        public override void SendKick(string reason, bool sync) {
            reason = CleanupColors(reason);
        }


        public override void SendChangeModel(byte id, string model) {
        }

        bool sentMOTD;
        public override void SendMotd(string motd) {
            if (sentMOTD) return; // TODO work out how to properly resend map
            sentMOTD = true;
            Send(MakeLogin(motd));
        }

        public override void SendPing() {
            Send(new byte[] { OPCODE_PING });
        }

        public override void SendSpawnEntity(byte id, string name, string skin, Position pos, Orientation rot) {
            name = CleanupColors(name);
            name = name.Replace('&', '§');
            skin = skin.Replace('&', '§');

            if (id == Entities.SelfID) {
                Send(MakeSelfMoveLook(pos, rot));
            } else {
                Send(MakeNamedAdd(id, name, skin, pos, rot));
            }
        }

        byte[] GetBlocks(Level level)
        {
            // NOTE indev client always overwrites bottom 2 layers with lava.. ?
            byte[] blocks = new byte[level.blocks.Length];
            int i = level.PosToInt(0, 2, 0);
            //for (int j = 0; j < i; j++) blocks[j] = Block.Bedrock;


            // TODO TERRIBLY AWFULLY EXTREMELY SLOW
            for (int y = 0; y < level.Height - 2; y++)
                for (int z = 0; z < level.Length; z++)
                    for (int x = 0; x < level.Width; x++)
                    {
                        blocks[i++] = (byte)level.FastGetBlock((ushort)x, (ushort)y, (ushort)z);
                    }
            return blocks;
        }

        public override void SendLevel(Level prev, Level level) {

            // TODO what even this
            byte[] C_blks = CompressData(GetBlocks(level));
            byte[] C_meta = CompressData(new byte[level.blocks.Length]);
            byte[] tmp = new byte[4];

            using (MemoryStream ms = new MemoryStream())
            {
                byte[] map_data = new byte[1 + 4 + 4 + 4];
                map_data[0] = OPCODE_PRE_CHUNK;
                NetUtils.WriteI32(C_blks.Length, map_data, 1);
                NetUtils.WriteI32(C_meta.Length, map_data, 5);
                NetUtils.WriteI32(100, map_data, 9); // TODO what even is this
                ms.Write(map_data, 0, map_data.Length);

                // TODO this seems wrong
                NetUtils.WriteI32(C_blks.Length, tmp, 0);
                ms.Write(tmp, 0, tmp.Length);
                ms.Write(C_blks, 0, C_blks.Length);

                NetUtils.WriteI32(C_meta.Length, tmp, 0);
                ms.Write(tmp, 0, tmp.Length);
                ms.Write(C_meta, 0, C_meta.Length);

                Send(ms.ToArray());
            }

            byte[] final = new byte[1 + 4 + 4 + 4 + 4 + 4];
            final[0] = OPCODE_CHUNK;

            final[1] = 0x01;
            // 4 bytes ??
            // 01 00 00 00 - 128x64x128 world
            // 01 01 00 00 - 256x64x256 world
            // 01 02 00 00 - 512x64x512 world
            NetUtils.WriteI32(level.Width, final,  5);
            NetUtils.WriteI32(level.Height, final,  9);
            NetUtils.WriteI32(level.Length, final, 13);
            // 4 bytes ???? checksum???
            //final[19] = 0x01; final[20] = 0x2E;
            Send(final);

            SendSetSpawnpoint(level.SpawnPos, default(Orientation));
        }

        public override void SendSetSpawnpoint(Position pos, Orientation rot)
        {
            byte[] spawn = new byte[1 + 4 + 4 + 4];
            spawn[0] = OPCODE_SPAWN_POSITION;
            NetUtils.WriteI32(pos.BlockX, spawn, 1);
            NetUtils.WriteI32(pos.BlockY, spawn, 5);
            NetUtils.WriteI32(pos.BlockZ, spawn, 9);
            Send(spawn);
        }

        byte[] CompressData(byte[] data)
        {
            using (MemoryStream dst = new MemoryStream())
            {
                using (GZipStream gz = new GZipStream(dst, CompressionMode.Compress, true))
                {
                    byte[] buffer = new byte[4];
                    NetUtils.WriteI32(data.Length, buffer, 0);
                    gz.Write(buffer, 0, 4);

                    gz.Write(data, 0, data.Length);
                }
                return dst.ToArray();
            }
        }

        byte[] MakeHandshake(string serverID) {
            int dataLen = 1 + 2 + CalcStringLength(serverID);
            byte[] data = new byte[dataLen];

            data[0] = OPCODE_HANDSHAKE;
            WriteString(data, 1, serverID);
            return data;
        }

        byte[] MakeLogin(string motd) {
            int nameLen = CalcStringLength(Server.Config.Name);
            int motdLen = CalcStringLength(motd);
            int dataLen = 1 + 14 + (2 + nameLen) + (2 + motdLen);
            byte[] data = new byte[dataLen];

            data[0] = OPCODE_LOGIN;
            // TODO not sure what first 14 bytes of data are
            // bytes 0-4 look like initial world time though?
            //   NetUtils.WriteI32(2, data, PROTOCOL_VERSION);
            WriteString(data, 1 + 14,               Server.Config.Name);
            WriteString(data, 1 + 14 + 2 + nameLen, motd);
            return data;
        }

        byte[] MakeChat(string text) {
            int textLen = CalcStringLength(text);
            byte[] data = new byte[1 + 2 + textLen];

            data[0] = OPCODE_CHAT;
            WriteString(data, 1 , text);
            return data;
        }

        byte[] MakeSelfMoveLook(Position pos, Orientation rot) {
            byte[] data = new byte[1 + 4 + 4 + 4 + 4 + 4 + 4 + 1];
            float yaw   = rot.RotY  * 360.0f / 256.0f;
            float pitch = rot.HeadX * 360.0f / 256.0f;
            data[0] = OPCODE_SELF_MOVE_LOOK;

            pos.Y += 83; // TODO not sure why this much 
            pos.Y += WORLD_SHIFT_COORDS;

            WriteF32(pos.X / 32.0f, data,  1);
            WriteF32(pos.Y / 32.0f, data,  5); // stance?
            WriteF32(pos.Y / 32.0f, data,  9);
            WriteF32(pos.Z / 32.0f, data, 13);

            WriteF32(yaw,   data, 17);
            WriteF32(pitch, data, 21);
            data[25] = 1;
            return data;
        }

        byte[] MakeBlockDig(byte status, int x, int y, int z) {
            byte[] data = new byte[1 + 1 + 4 + 1 + 4 + 1];
            data[0] = OPCODE_BLOCK_DIG;

            data[1] = status;
            WriteI32(x, data, 2);
            data[6] = (byte)y;
            WriteI32(z, data, 7);
            data[11] = 1;
            return data;
        }

        

        byte[] MakeNamedAdd(byte id, string name, string skin, Position pos, Orientation rot) {
            int nameLen = CalcStringLength(name);
            int dataLen = 1 + 4 + (2 + nameLen) + (4 + 4 + 4) + (1 + 1) + 2;
            byte[] data = new byte[dataLen];
            // TODO fixes Y kinda
            pos.Y -= 19;
            pos.Y += WORLD_SHIFT_COORDS;

            data[0] = OPCODE_NAMED_ADD;
            WriteI32(id, data, 1);
            WriteString(data, 5, name);

            WriteI32(pos.X, data,  7 + nameLen);
            WriteI32(pos.Y, data, 11 + nameLen);
            WriteI32(pos.Z, data, 15 + nameLen);

            data[19 + nameLen] = (byte)(rot.RotY + 128); // TODO fixed yaw kinda
            data[20 + nameLen] = rot.HeadX;
            WriteU16(0, data, 21 + nameLen); // current item
            return data;
        }

        byte[] MakeEntityTeleport(byte id, Position pos, Orientation rot) {
            int dataLen = 1 + 4 + (4 + 4 + 4) + (1 + 1);
            byte[] data = new byte[dataLen];
            data[0] = OPCODE_TELEPORT;
            // TODO fixes Y kinda
            pos.Y -= 19;
            pos.Y += WORLD_SHIFT_COORDS;

            WriteI32(id, data, 1);
            WriteI32(pos.X, data,  5);
            WriteI32(pos.Y, data,  9);
            WriteI32(pos.Z, data, 13);

            data[17] = (byte)(rot.RotY + 128); // TODO fixed yaw kinda
            data[18] = rot.HeadX;
            return data;
        }

        string CleanupColors(string value) {
            return LineWrapper.CleanupColors(value, false, false);
        }

        public override string ClientName() {
            return "Indev";
        }
        
        /// <summary> Converts the given block ID into a raw block ID that can be sent to this player </summary>
        public override BlockID ConvertBlock(BlockID block) {
            BlockID raw;
            if (block >= Block.Extended) {
                raw = Block.ToRaw(block);
            } else {
                raw = Block.Convert(block);
                // show invalid physics blocks as Orange
                if (raw >= Block.CPE_COUNT) raw = Block.Orange;
            }
            if (raw > MaxRawBlock) raw = player.level.GetFallback(block);
            
            // Check if a custom block replaced a core block
            //  If so, assume fallback is the better block to display
            if (raw < Block.CPE_COUNT) {
                BlockDefinition def = player.level.CustomBlockDefs[raw];
                if (def != null) raw = def.FallBack;
            }
            
            raw = fallback[(byte)raw];
            return raw;
        }

        public override void SendAddTabEntry(byte id, string name, string nick, string group, byte groupRank)
        {
            throw new NotImplementedException();
        }

        public override void SendRemoveTabEntry(byte id)
        {
            throw new NotImplementedException();
        }

        public override byte[] MakeBulkBlockchange(BufferedBlockSender buffer) {
            int size = 1 + 4 + 1 + 4 + 1 + 1;
            byte[] data = new byte[size * buffer.count];
            Level level = buffer.level;

            for (int i = 0; i < buffer.count; i++)
            {
                int index = buffer.indices[i];
                int x = (index % level.Width);
                int y = (index / level.Width) / level.Length;
                int z = (index / level.Width) % level.Length;

                WriteBlockChange(data, i * size, (byte)buffer.blocks[i], x, y, z);
            }
            return data;
        }

        void WriteBlockChange(byte[] data, int offset, byte block, int x, int y, int z)
        {
            y += WORLD_SHIFT_BLOCKS;
            data[offset + 0] = OPCODE_BLOCK_CHANGE;
            WriteI32(x, data, offset + 1);
            data[offset + 5] = (byte)y;
            WriteI32(z, data, offset + 6);
            data[offset + 10] = block;
            data[offset + 11] = 0; // metadata
        }

        public unsafe override void UpdatePlayerPositions() {
            Player[] players = PlayerInfo.Online.Items;
            Player dst = player;
            
            foreach (Player p in players) {
                if (dst == p || dst.level != p.level || !dst.CanSeeEntity(p)) continue;
                
                Orientation rot = p.Rot;
                // TEMP HACK
                Position delta  = Entities.GetDelta(p.tempPos, p.lastPos, p.hasExtPositions);
                bool posChanged = delta.X != 0 || delta.Y != 0 || delta.Z != 0;
                bool oriChanged = rot.RotY != p.lastRot.RotY || rot.HeadX != p.lastRot.HeadX;
                if (posChanged || oriChanged)
                    SendTeleport(p.id, p.tempPos, rot);
            }
        }

        public override void SendBlockchange(ushort x, ushort y, ushort z, BlockID block) {
            byte[] packet = new byte[1 + 4 + 1 + 4 + 1 + 1];
            byte raw = (byte)ConvertBlock(block);
            WriteBlockChange(packet, 0, raw, x, y, z);
            Send(packet);
        }
    }
}