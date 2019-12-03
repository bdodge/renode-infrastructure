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
    public class DoverLed : IDoubleWordPeripheral, IKnownSize
    {
        public const uint DOVER_LEDS = 4;
        
        public DoverLed ()
        {
            leds = new Led[DOVER_LEDS];
            for(int i = 0; i < DOVER_LEDS; i++)
            {
                leds[i] = new Led();
                leds[i].Control = 0;
                leds[i].Status = 0;
            }
        }
        public long Size
        {
            get
            {
                return 4096; //DOVER_LEDS * Led.bytes_per_led;
            }
        }

        #region IDoubleWordPeripheral implementation
        public uint ReadDoubleWord (long offset)
        {
            uint led;
            uint regdex;
            
            led = (uint)offset / Led.bytes_per_led;
            regdex = (uint)offset - (led * Led.bytes_per_led);
            
            this.Log(LogLevel.Debug, "Read LED 0x{0:X} led:{1} off:0x{2:X}", offset, led, regdex);
            
            if (led >= DOVER_LEDS)
            {
                this.Log(LogLevel.Warning, "Invalid led {0}", led);
                return 0;
            }
            
            switch ((Led.Offset)regdex)
            {
            case Led.Offset.Control:
                return leds[led].Control;
            case Led.Offset.Status:
                return leds[led].Status;
            }
            this.Log(LogLevel.Warning, "Read from invalid offset {0}", regdex);
            return 0;
        }

        public void WriteDoubleWord (long offset, uint value)
        {
            uint led;
            uint regdex;
            
            led = (uint)offset / Led.bytes_per_led;
            regdex = (uint)offset - (led * Led.bytes_per_led);
            
            this.Log(LogLevel.Debug, "Write LED 0x{0:X} led:{1} off:0x{2:X}", offset, led, regdex);

            if (led >= DOVER_LEDS)
            {
                this.Log(LogLevel.Warning, "Invalid led {0}", led);
                return;
            }
            
            switch ((Led.Offset)regdex)
            {
            case Led.Offset.Control:
                leds[led].Control = value;
                break;
            case Led.Offset.Status:
                leds[led].Status = value;
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
  
        private struct Led
        {
            public uint Control;
            public uint Status;
            
            public enum Offset:uint //register offsets in a single led
            {
                Control = 0x00,
                Status = 0x04
            }
            
            public const uint bytes_per_led = 0x8;
        }
        
        private Led[] leds; 
        
    }
}

