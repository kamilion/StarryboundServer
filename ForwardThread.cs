﻿/* 
 * Starrybound Server
 * Copyright 2013, Avilance Ltd
 * Created by Zidonuke (zidonuke@gmail.com) and Crashdoom (crashdoom@avilance.com)
 * 
 * This file is a part of Starrybound Server.
 * Starrybound Server is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
 * Starrybound Server is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
 * You should have received a copy of the GNU General Public License along with Starrybound Server. If not, see http://www.gnu.org/licenses/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using com.avilance.Starrybound.Util;
using com.avilance.Starrybound.Extensions;
using Ionic.Zlib;
using com.avilance.Starrybound.Packets;

namespace com.avilance.Starrybound
{
    class ForwardThread
    {
        BinaryReader mInput;
        BinaryWriter mOutput;
        ClientThread mParent;
        Direction mDirection;
        private string passwordSalt;

        public ForwardThread(ClientThread aParent, BinaryReader aInput, BinaryWriter aOutput, Direction aDirection) {
            this.mParent = aParent;
            this.mInput = aInput;
            this.mOutput = aOutput;
            this.mDirection = aDirection;
        }

        public void run()
        {
            try
            {
                for (;;)
                {
                    if (!this.mParent.connectionAlive)
                    {
                        this.mParent.forceDisconnect("Connection Lost");
                        return;
                    }


                    if (this.mParent.kickTargetTimestamp != 0)
                    {
                        if (this.mParent.kickTargetTimestamp < Utils.getTimestamp())
                        {
                            this.mParent.forceDisconnect("Kicked from server");
                            return;
                        }
                        continue;
                    }

                    #region Process Packet
                    //Packet ID and Vaildity Check.
                    uint temp = this.mInput.ReadVarUInt32();
                    if (temp < 1 || temp > 48)
                    {
                        this.mParent.errorDisconnect(mDirection, "Sent invalid packet ID [" + temp + "].");
                        return;
                    }
                    Packet packetID = (Packet)temp;
                    string packetName = packetID.ToString();

                    //Packet Size and Compression Check.
                    bool compressed = false;
                    int packetSize = this.mInput.ReadVarInt32();
                    if (packetSize < 0)
                    {
                        packetSize = -packetSize;
                        compressed = true;
                    }

                    //Create buffer for forwarding
                    byte[] dataBuffer = this.mInput.ReadFully(packetSize);

                    //Do decompression
                    MemoryStream ms = new MemoryStream();
                    if (compressed)
                    {
                        ZlibStream compressedStream = new ZlibStream(new MemoryStream(dataBuffer), CompressionMode.Decompress);
                        byte[] buffer = new byte[32768];
                        for (;;)
                        {
                            int read = compressedStream.Read(buffer, 0, buffer.Length);
                            if (read <= 0)
                                break;
                            ms.Write(buffer, 0, read);
                        }
                        ms.Seek(0, SeekOrigin.Begin);
                    }
                    else
                    {
                        ms = new MemoryStream(dataBuffer);
                    }

                    //Create packet parser
                    BinaryReader packetData = new BinaryReader(ms);

                    //Return data for packet processor
                    object returnData = true;
                    #endregion

                    #region Handle Packet Data
                    //Process packet
                    if (packetID == Packet.ChatSend && mDirection == Direction.Client)
                    {
                        returnData = new Packet11ChatSend(this.mParent, packetData, this.mDirection).onReceive();
                    }
                    else if (packetID == Packet.ChatReceive && mDirection == Direction.Server)
                    {
                        returnData = new Packet5ChatReceive(this.mParent, packetData, this.mDirection).onReceive();
                    }
                    else if (packetID == Packet.ProtocolVersion && mDirection == Direction.Server)
                    {
                        uint protocolVersion = packetData.ReadUInt32BE();
                        if(protocolVersion != StarryboundServer.ProtocolVersion)
                        {
                            MemoryStream packet = new MemoryStream();
                            BinaryWriter packetWrite = new BinaryWriter(packet);
                            packetWrite.WriteBE(protocolVersion);
                            this.mParent.sendClientPacket(Packet.ProtocolVersion, packet.ToArray());
                            
                            Packet2ConnectResponse packet2 = new Packet2ConnectResponse(this.mParent, false, Util.Direction.Client);
                            packet2.prepare("Starrybound Server was unable to handle the parent server protocol version.");
                            packet2.onSend();
                            returnData = false;
                            this.mParent.errorDisconnect(mDirection, "Cannot handle protocol version [" + protocolVersion + "].");
                        }
                    }
                    else if (packetID == Packet.ClientConnect && mDirection == Direction.Client)
                    {
                        this.mParent.clientState = ClientState.PendingAuthentication;
                        returnData = new Packet7ClientConnect(this.mParent, packetData, this.mDirection).onReceive();
                        MemoryStream packet = new MemoryStream();
                        BinaryWriter packetWrite = new BinaryWriter(packet);

                        passwordSalt = Utils.GenerateSecureSalt();
                        packetWrite.WriteStarString("");
                        packetWrite.WriteStarString(passwordSalt);
                        packetWrite.WriteBE(StarryboundServer.config.passwordRounds);
                        this.mParent.sendClientPacket(Packet.HandshakeChallenge, packet.ToArray());
                    }
                    else if (packetID == Packet.HandshakeChallenge && mDirection == Direction.Server)
                    {
                        string claimMessage = packetData.ReadString();
                        string passwordSalt = packetData.ReadStarString();
                        int passwordRounds = packetData.ReadInt32BE();

                        MemoryStream packet = new MemoryStream();
                        BinaryWriter packetWrite = new BinaryWriter(packet);
                        string passwordHash = Utils.StarHashPassword(StarryboundServer.config.serverPass, StarryboundServer.config.serverAccount + passwordSalt, passwordRounds);
                        packetWrite.WriteStarString("");
                        packetWrite.WriteStarString(passwordHash);
                        this.mParent.sendServerPacket(Packet.HandshakeResponse, packet.ToArray());

                        returnData = false;
                    }
                    else if (packetID == Packet.HandshakeResponse && mDirection == Direction.Client)
                    {
                        string claimResponse = packetData.ReadStarString();
                        string passwordHash = packetData.ReadStarString();

                        string verifyHash = Utils.StarHashPassword(StarryboundServer.config.proxyPass, this.mParent.playerData.account + passwordSalt, StarryboundServer.config.passwordRounds);
                        if(passwordHash != verifyHash)
                        {
                            Packet2ConnectResponse packet = new Packet2ConnectResponse(this.mParent, false, Util.Direction.Client);
                            packet.prepare("Your password was incorrect.");
                            packet.onSend();
                            this.mParent.forceDisconnect("Password was incorrect");
                        }

                        this.mParent.clientState = ClientState.PendingConnectResponse;
                        returnData = false;
                    }
                    else if (packetID == Packet.ConnectResponse && mDirection == Direction.Server)
                    {
                        while (this.mParent.clientState != ClientState.PendingConnectResponse) { } //TODO: needs timeout
                        returnData = new Packet2ConnectResponse(this.mParent, packetData, this.mDirection).onReceive();
                    }
                    else if (packetID == Packet.WorldStart && mDirection == Direction.Server)
                    {
                        byte[] data1 = packetData.ReadStarByteArray();
                        byte[] data2 = packetData.ReadStarByteArray();
                        byte[] data3 = packetData.ReadStarByteArray();
                        byte[] data4 = packetData.ReadStarByteArray();
                        float spawnX = packetData.ReadSingleBE();
                        float spawnY = packetData.ReadSingleBE();
                        uint mapParamsSize = packetData.ReadVarUInt32();
                        List<string> mapParamsKeys = new List<string>();
                        List<object> mapParamsValues = new List<object>();
                        for (int i = 0; i < mapParamsSize; i++)
                        {
                            mapParamsKeys.Add(packetData.ReadStarString());
                            mapParamsValues.Add(packetData.ReadStarVariant());
                        }
                        uint uint1 = packetData.ReadUInt32BE();
                        bool bool1 = packetData.ReadBoolean();
                    }
                    else if (packetID == Packet.WorldStop && mDirection == Direction.Server)
                    {
                        string status = packetData.ReadStarString();
                    }
                    else if (packetID == Packet.WarpCommand && mDirection == Direction.Client)
                    {
                        uint warp = packetData.ReadUInt32BE();
                        string sector = packetData.ReadStarString();
                        int x = packetData.ReadInt32BE();
                        int y = packetData.ReadInt32BE();
                        int z = packetData.ReadInt32BE();
                        int planet = packetData.ReadInt32BE();
                        int satellite = packetData.ReadInt32BE();
                        string player = packetData.ReadStarString();
                        StarryboundServer.logInfo("[" + this.mParent.playerData.client + "] WarpCommand [" + warp + "][" + sector + ":" + x + ":" + y + ":" + z + ":" + planet + ":" + satellite + "][" + player + "]");
                    }
                    else if(packetID == Packet.DamageTile)
                    {
                        if (!this.mParent.playerData.canBuild) continue;
                    }
                    else if (packetID == Packet.DamageTileGroup)
                    {
                        if (!this.mParent.playerData.canBuild) continue;
                    }
#endregion

                    #if DEBUG
                    if (StarryboundServer.config.logLevel == LogType.Debug)
                    {
                        if (packetData.BaseStream.Position != ms.Length)
                            StarryboundServer.logDebug("ForwardThread", "[" + this.mParent.playerData.client + "] [" + this.mDirection.ToString() + "][" + packetName + ":" + packetID + "] failed parse (" + packetData.BaseStream.Position + " != " + ms.Length + ")");
                        StarryboundServer.logDebug("ForwardThread", "[" + this.mParent.playerData.client + "] [" + this.mDirection.ToString() + "][" + packetName + ":" + packetID + "] Dumping " + ms.Length + " bytes: " + Utils.ByteArrayToString(ms.ToArray()));
                    }
                    #endif

                    //Check return data
                    if (returnData is Boolean)
                    {
                        if ((Boolean)returnData == false) continue;
                    }
                    else if (returnData is int)
                    {
                        if ((int)returnData == -1)
                        {
                            this.mParent.forceDisconnect("Command processor requested to drop client");
                        }
                    }

                    #region Forward Packet
                    //Write data to dest
                    this.mOutput.WriteVarUInt32((uint)packetID);
                    if (compressed)
                    {
                        this.mOutput.WriteVarInt32(-packetSize);
                        this.mOutput.Write(dataBuffer, 0, packetSize);
                    }
                    else
                    {
                        this.mOutput.WriteVarInt32(packetSize);
                        this.mOutput.Write(dataBuffer, 0, packetSize);
                    }
                    this.mOutput.Flush();
                    #endregion

                    //If disconnect was forwarded to client, lets disconnect.
                    if(packetID == Packet.ServerDisconnect && mDirection == Direction.Server)
                    {
                        this.mParent.forceDisconnect();
                    }
                }
            }
            catch (Exception e)
            {
                this.mParent.errorDisconnect(mDirection, "ForwardThread Error: " + e.Message);
            }
        }
    }
}
