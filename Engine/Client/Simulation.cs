﻿using System.Collections.Generic;        
using Lockstep.Client.Interfaces;
using Lockstep.Core.Data;
using Lockstep.Core.Interfaces;
using Lockstep.Network.Messages;

namespace Lockstep.Client
{                    
    public class Simulation
    {                                        
        /// <summary>
        /// Amount of ticks until a command gets executed locally
        /// </summary>
        public uint LagCompensation { get; set; }

        public bool Running { get; private set; }

        public PlayerId LocalPlayerId { get; private set; } 

        private float _tickDt;
        private float _accumulatedTime;
        private uint _lastValidatedFrame;

        private readonly IWorld _world;
        private readonly ICommandBuffer _remoteCommandBuffer;                         

        private readonly Dictionary<PlayerId, List<ICommand>> _commandCache = new Dictionary<PlayerId, List<ICommand>>();

        public Simulation(IWorld world, ICommandBuffer remoteCommandBuffer)
        {
            _world = world;     
            _remoteCommandBuffer = remoteCommandBuffer;
        }

        public void Initialize(Init init)
        {             
            _tickDt = 1000f / init.TargetFPS;
            LocalPlayerId = init.PlayerID;

            _commandCache.Add(LocalPlayerId, new List<ICommand>());

            _world.Initialize(LocalPlayerId);

            Running = true;
        }   

        public void Execute(ICommand command)
        {
            if (!Running)
            {
                return;
            }

            lock (_commandCache)
            {
                _commandCache[LocalPlayerId].Add(command);
            }            
        }

        public void Update(float deltaTime)
        {                             
            if (!Running)                        
            {
                return;
            }

            SyncCommandBuffer();

            _accumulatedTime += deltaTime; 

            while (_accumulatedTime >= _tickDt)
            {                                                                                
                Tick();

                _accumulatedTime -= _tickDt;
            }                 
        }

        private void Tick()
        {                              
            lock (_commandCache)
            {
                if (_commandCache[LocalPlayerId].Count > 0)
                {
                    _world.AddInput(_world.CurrentTick + LagCompensation, _commandCache);
                    _remoteCommandBuffer.Insert(_world.CurrentTick + LagCompensation, LocalPlayerId, _commandCache[LocalPlayerId].ToArray());

                    _commandCache[LocalPlayerId].Clear();  
                }
            }    

            _world.Tick();     
        }

        private void SyncCommandBuffer()
        {
            var currentRemoteFrame = _remoteCommandBuffer.LastInsertedFrame;
  
            if (_lastValidatedFrame < currentRemoteFrame)
            {
                _world.Services.Get<ILogService>().Warn("Keep up from "+ _lastValidatedFrame + " to " + currentRemoteFrame);
                //We guess everything was predicted correctly (except the last received frame)
                var firstMispredictedFrame = currentRemoteFrame; 
                                                                        
                for (var remoteFrame = _lastValidatedFrame + 1; remoteFrame <= currentRemoteFrame; remoteFrame++)
                {
                    //All frames that have no commands were predicted correctly => increase remote frame
                    var allPlayerCommands = _remoteCommandBuffer.Get(remoteFrame);
                    if (allPlayerCommands.Count == 0)
                    {
                        continue;
                    }

                    if (firstMispredictedFrame > remoteFrame)
                    {
                        //Set the first mispredicted frame to the first frame which contains commands
                        firstMispredictedFrame = remoteFrame;
                    }             

                    //TODO: if command contains entity-ids (which can be predicted) and due to rollback we generated local ids, the command's entity-ids have to be adjusted
                    //TODO: order by timestamp in case of multiple commands in the same frame => if commands intersect, the first one should win, timestamp should be added by server, RTT has to be considered, ordering is enough, validation should take place in the simulation(core)                                                                    
                    _world.AddInput(remoteFrame, allPlayerCommands);   
                }

                //Only rollback if the mispredicted frame was in the past (the frame can be in the future due to high lag compensation)
                if (firstMispredictedFrame <= _world.CurrentTick)
                {      
                    var targetTick = _world.CurrentTick;  
                                                                                                                                                                     
                    _world.RevertToTick(firstMispredictedFrame);

                    var validInputFrame = firstMispredictedFrame;

                    //Execute all commands again, beginning from the first frame that contains remote input up to our last local state
                    while (validInputFrame <= targetTick)
                    {   
                        _world.Tick();
                        validInputFrame++;
                    }
                }

                _lastValidatedFrame = currentRemoteFrame;
            }   
        }
    }
}