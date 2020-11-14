﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HunterPie.Core;
using Plugin.Sync.Model;
using Plugin.Sync.Util;

namespace Plugin.Sync.Server
{
    public class PushService
    {
        public string SessionId { get; set; }

        private readonly object locker = new object();
        private readonly ConcurrentDictionary<int, MonsterModel> cachedMonsters = new ConcurrentDictionary<int, MonsterModel>();
        private readonly List<MonsterModel> pushQueue = new List<MonsterModel>();
        private readonly SyncServerClient client = new SyncServerClient();
        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private Thread thread;

        public void SetState(bool state)
        {
            var isRunning = this.thread?.IsAlive ?? false;
            // same state, don't do anything
            if (isRunning == state) return;
            if (state)
            {
                this.cancellationTokenSource.Cancel();
                this.cancellationTokenSource = new CancellationTokenSource();
                var scanRef = new ThreadStart(() => PushLoop(this.cancellationTokenSource.Token));
                this.thread = new Thread(scanRef) {Name = "SyncPlugin_PushLoop"};
                lock (this.locker)
                {
                    this.pushQueue.Clear();
                    this.cachedMonsters.Clear();
                }
                this.thread.Start();
            }
            else
            {
                this.cancellationTokenSource.Cancel();
                lock (this.locker)
                {
                    this.pushQueue.Clear();
                    this.cachedMonsters.Clear();
                }
            }
        }

        public void PushMonster(Monster monster, int index)
        {
            if (string.IsNullOrEmpty(this.SessionId))
            {
                return;
            }

            var mappedMonster = MapMonster(monster, index);
            PushMonster(mappedMonster);
        }

        public void PushMonster(MonsterModel model)
        {
            if (string.IsNullOrEmpty(this.SessionId))
            {
                return;
            }

            lock (this.locker)
            {
                if (this.cachedMonsters.TryGetValue(model.Index, out var existingMonster) && existingMonster.Equals(model))
                {
                    return;
                }

                // TODO: diffing
                this.cachedMonsters[model.Index] = model;
                this.pushQueue.Add(model);
            }
        }

        private async void PushLoop(CancellationToken token)
        {
            var retryCount = 0;
            var sw = new Stopwatch();

            while (true)
            {
                if (string.IsNullOrEmpty(this.SessionId))
                {
                    await Task.Delay(50);
                    continue;
                }

                try
                {
                    List<MonsterModel> monsters;

                    lock (this.locker)
                    {
                        monsters = this.pushQueue
                            .GroupBy(m => m.Index)
                            .Select(g => g.Last())
                            .OrderBy(m => m.Index)
                            .ToList();
                        this.pushQueue.Clear();
                    }

                    if (token.IsCancellationRequested)
                    {
                        Logger.Debug("Push loop stopped");
                        return;
                    }

                    // wait for changes to appear
                    if (!monsters.Any())
                    {
                        await Task.Delay(50);
                        continue;
                    }

                    await this.client.PushChangedMonsters(this.SessionId, monsters);
                    Util.Logger.Trace($"PUSH monsters: {monsters.Count} ({sw.ElapsedMilliseconds} ms from last push)");
                    sw.Restart();
                    if (retryCount != 0)
                    {
                        retryCount = 0;
                        Util.Logger.Log("Connection restored");
                    }
                    // throttling
                    await Task.Delay(300);
                }
                catch (Exception ex)
                {
                    Util.Logger.Error($"Error on pushing monsters to server. Will retry after 10 sec when new data is available ({retryCount++}/10): {ex.Message}");
                    await Task.Delay(10000);
                    if (retryCount == 10)
                    {
                        Util.Logger.Log("Pushing stopped - no monster data for other members.");
                        return;
                    }
                }
            }
        }

        private static MonsterModel MapMonster(Monster monster, int index)
        {
            return new MonsterModel
            {
                Id = monster.Id,
                Index = index,
                Parts = monster.Parts.Select(MonsterPartModel.FromDomain).ToList(),
                Ailments = monster.Ailments.Select(AilmentModel.FromDomain).ToList()
            };
        }
    }
}
