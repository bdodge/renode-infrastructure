//
// Copyright (c) 2010-2018 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Utilities;
using System.Threading;

namespace Antmicro.Renode.Peripherals.DMA
{
    public class DoverDma : IDoubleWordPeripheral, IKnownSize
    {
        public const uint DOVER_DMA_CHANNELS = 4;
        
        public DoverDma ()
        {
            channels = new Channel[DOVER_DMA_CHANNELS];
            for(int i = 0; i < DOVER_DMA_CHANNELS; i++)
            {
                channels[i] = new Channel();
                channels[i].Control = 0;
                channels[i].Status = 0;
            }
            IRQ = new GPIO();
        }

        public GPIO IRQ {get; private set;}

        public long Size
        {
            get
            {
                return DOVER_DMA_CHANNELS * Channel.bytes_per_channel;
            }
        }

        #region IDoubleWordPeripheral implementation
        public uint ReadDoubleWord (long offset)
        {
            uint channel;
            uint regdex;
            
            channel = (uint)offset / Channel.bytes_per_channel;
            regdex = (uint)offset - (channel * Channel.bytes_per_channel);

            this.Log(LogLevel.Debug, "Read DMA 0x{0:X} chan:{1} reg:0x{2:X}", offset, channel, regdex);
            
            if (channel >= DOVER_DMA_CHANNELS)
            {
                this.Log(LogLevel.Warning, "Invalid channel {0}", channel);
                return 0;
            }
            
            switch ((Channel.Offset)regdex)
            {
            case Channel.Offset.Control:
                return channels[channel].Control;
            case Channel.Offset.Status:
                return channels[channel].Status;
            case Channel.Offset.DestinationAddress:
                return channels[channel].DestinationAddress;
            case Channel.Offset.SourceAddress:
                return channels[channel].SourceAddress;
            case Channel.Offset.TransferCount:
                return channels[channel].TransferCount;
            case Channel.Offset.TransferMode:
                return channels[channel].TransferMode;
            }
            this.Log(LogLevel.Warning, "Read from invalid offset {0}", regdex);
            return 0;
        }

        public void WriteDoubleWord (long offset, uint value)
        {
            uint channel;
            uint regdex;
            
            channel = (uint)offset / Channel.bytes_per_channel;
            regdex = (uint)offset - (channel * Channel.bytes_per_channel);
            
            this.Log(LogLevel.Debug, "Write DMA 0x{0:X} chan:{1} reg:0x{2:X}", offset, channel, regdex);

            if (channel >= DOVER_DMA_CHANNELS)
            {
                this.Log(LogLevel.Warning, "Invalid channel {0}", channel);
                return;
            }
            
            switch ((Channel.Offset)regdex)
            {
            case Channel.Offset.Control:
                channels[channel].Control = value;
                break;
            case Channel.Offset.Status:
                channels[channel].Status = value;
                break;
            case Channel.Offset.DestinationAddress:
                channels[channel].DestinationAddress = value;
                break;
            case Channel.Offset.SourceAddress:
                channels[channel].SourceAddress = value;
                break;
            case Channel.Offset.TransferCount:
                channels[channel].TransferCount = value;
                break;
            case Channel.Offset.TransferMode:
                channels[channel].TransferMode = value;
                break;
            default:
                this.Log(LogLevel.Warning, "Write to invalid offset {0}", regdex);
                break;
            }            
        }
        #endregion

        public void Reset ()
        {
            throw new NotImplementedException ();
        }
  
        private struct Channel
        {
            public uint Control;
            public uint Status;
            public uint SourceAddress;
            public uint DestinationAddress;
            public uint TransferCount;
            public uint TransferMode;
            
            public enum Offset:uint //register offsets in a single channel
            {
                Control = 0x00,
                Status = 0x04,
                SourceAddress = 0x08,
                DestinationAddress = 0x0C,
                TransferCount = 0x10,
                TransferMode = 0x14
            }
            
            public const uint bytes_per_channel = 0x20;
        }
        
        private Channel[] channels; 
        
    }
}

