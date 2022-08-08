﻿using common;
using wServer.core;
using wServer.networking;

namespace wServer.core.net.handlers
{
    public class MoveHandler : IMessageHandler
    {
        public override PacketId MessageId => PacketId.MOVE;

        public override void Handle(Client client, NReader rdr, ref TickTime tickTime)
        {
            var tickId = rdr.ReadInt32();
            var time = rdr.ReadInt32();
            var newPosition = Position.Read(rdr);
            var moveRecords = new TimedPosition[rdr.ReadInt16()];
            for (var i = 0; i < moveRecords.Length; i++)
                moveRecords[i] = TimedPosition.Read(rdr);

            var player = client.Player;

            if (player == null || player.World == null)
                return;

            player.MoveReceived(tickTime, time, tickId);

            var newX = newPosition.X;
            var newY = newPosition.Y;

            if (newX != -1 && newX != player.X || newY != -1 && newY != player.Y)
            {
                if (!player.World.Map.Contains(newX, newY))
                {
                    player.Client.Disconnect("Out of map bounds");
                    return;
                }

                player.Move(newX, newY);
                player.PlayerUpdate.UpdateTiles = true;
                if (player.IsNoClipping())
                    player.Client.Disconnect("No clipping");
            }
        }
    }
}
