//
// Copyright (c) 2010-2019 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Linq;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Core.USB;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Utilities.Packets;
using System.Threading;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class MAX3421E : SimpleContainer<IUSBDevice>, IProvidesRegisterCollection<ByteRegisterCollection>, ISPIPeripheral, IDisposable
    {
        public MAX3421E(Machine machine) : base(machine)
        {
            IRQ = new GPIO();
            setupQueue = new Queue<byte>();
            receiveQueue = new Queue<byte>();
            sendQueue = new Queue<byte>();
            bumper = machine.ObtainManagedThread(BumpFrameNumber, BumpsPerSecond);

            RegistersCollection = new ByteRegisterCollection(this);

            DefineRegisters();
        }

        public override void Register(IUSBDevice peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            base.Register(peripheral, registrationPoint);

            // indicate the K state - full-speed device attached
            kStatus.Value = true;
            jStatus.Value = false;

            condetirq.Value = true;

            this.Log(LogLevel.Debug, "USB device connected to port {0}", registrationPoint.Address);
            UpdateInterrupts();
        }

        public override void Unregister(IUSBDevice peripheral)
        {
            base.Unregister(peripheral);

            condetirq.Value = true;
            UpdateInterrupts();
        }

        public void FinishTransmission()
        {
            this.Log(LogLevel.Noisy, "Transmission finished");
            state = State.Idle;
        }

        public override void Dispose()
        {
            bumper.Dispose();
        }

        public override void Reset()
        {
            RegistersCollection.Reset();
            UpdateInterrupts();

            lastRegister = 0;
            state = State.Idle;

            setupQueue.Clear();
            receiveQueue.Clear();
            sendQueue.Clear();
        }

        public byte Transmit(byte data)
        {
            this.Log(LogLevel.Noisy, "Received byte: 0x{0:X} in state {1}", data, state);
            byte result = 0;

            switch(state)
            {
            case State.Idle:
                HandleCommandByte(data);
                break;

            case State.Writing:
                this.Log(LogLevel.Noisy, "Writing value 0x{0:X} to register {1}", data, lastRegister);
                RegistersCollection.Write((long)lastRegister, data);
                break;

            case State.Reading:
                this.Log(LogLevel.Noisy, "Reading value from register {0}", lastRegister);
                result = RegistersCollection.Read((long)lastRegister);
                break;

            default:
                this.Log(LogLevel.Error, "Received byte 0x{0:X} in unexpected state: {1}. Ignoring it...", data, state);
                break;
            }

            this.Log(LogLevel.Noisy, "Returning byte: 0x{0:X}", result);
            return result;
        }

        public GPIO IRQ { get; }

        public ByteRegisterCollection RegistersCollection { get; }

        private void UpdateInterrupts()
        {
            var state = false;

            state |= (condetirq.Value && condetie.Value);
            state |= (busirq.Value && busie.Value);
            state |= (frameirq.Value && frameie.Value);
            state |= (hxfrdnirq.Value && hxfrdnie.Value);
            state |= (rcvdavirq.Value && rcvdaie.Value);
            state |= (sndbavirq.Value && sndbavie.Value);

            state  &= ie.Value;

            this.Log(LogLevel.Noisy, "Setting IRQ to {0}", state);
            IRQ.Set(state);
        }

        private void DefineRegisters()
        {
            RegisterType.Revision.Define(this)
                .WithValueField(0, 8, FieldMode.Read, valueProviderCallback: _ => ChipRevision)
            ;

            RegisterType.USBIrqPending.Define(this)
                .WithFlag(0, out var oscillatorOK, FieldMode.Read | FieldMode.WriteOneToClear, name: "Oscillator OK Interrupt Request")
            ;

            RegisterType.USBControl.Define(this)
                .WithFlag(5, name: "Chip Reset", changeCallback: (_, v) =>
                {
                    if(!v)
                    {
                        // software should test this IRQ after setting CHIPRES = 0
                        // in order to wait for oscillator and PLLs to stabilize
                        oscillatorOK.Value = true;
                        UpdateInterrupts();
                    }
                })
            ;

            RegisterType.CPUControl.Define(this)
                .WithFlag(0, out ie, name: "Interrupt Enable")
            ;

            RegisterType.HostIrqPending.Define(this, 0x8) //sndbavirq is set by default
                .WithFlag(0, out busirq, FieldMode.Read | FieldMode.WriteOneToClear, name: "Bus Event")
                .WithFlag(2, out rcvdavirq, FieldMode.Read | FieldMode.WriteOneToClear, name: "Receive Data Available Interrupt Request") // this should not go automatically from 1 to 0 when the fifo is empty, but should be explicitely cleared by the cpu
                .WithFlag(3, out sndbavirq, FieldMode.Read, name: "Send Data Buffer Available Interrupt Request") // this bit is cleared by writing to SNDBC register
                .WithFlag(5, out condetirq, FieldMode.Read | FieldMode.WriteOneToClear, name: "Peripheral Conect/Disconnect Interrupt Request")
                .WithFlag(6, out frameirq, FieldMode.Read | FieldMode.WriteOneToClear, name: "Frame Generator Interrupt Request")
                .WithFlag(7, out hxfrdnirq, FieldMode.Read | FieldMode.WriteOneToClear, name: "Host Transfer Done Interrupt Request")
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            RegisterType.HostIrqEnabled.Define(this)
                .WithFlag(0, out busie, name: "Bus Event")
                .WithFlag(2, out rcvdaie, name: "Receive Data Avilable Interrupt Enable")
                .WithFlag(3, out sndbavie, name: "Send Data Buffer Avilable Interrupt Enable")
                .WithFlag(5, out condetie, name: "Peripheral Conect/Disconnect Interrupt Enable")
                .WithFlag(6, out frameie, name: "Frame Generator Interrupt Enable")
                .WithFlag(7, out hxfrdnie, name: "Host Transfer Done Interrupt Enable")
                .WithWriteCallback((_, __) => UpdateInterrupts())
            ;

            RegisterType.HostResult.Define(this)
                .WithValueField(0, 4, name: "hrslt")
                .WithFlag(4, name: "rcvtogrd")
                .WithFlag(5, name: "sndtogrd")
                .WithFlag(6, out kStatus, name: "kstatus")
                .WithFlag(7, out jStatus, name: "jstatus")
            ;

            RegisterType.Mode.Define(this)
                .WithFlag(0, name: "host mode", writeCallback: (_, val) =>
                {
                    if(val)
                    {
                        bumper.Start();
                    }
                    else
                    {
                        bumper.Stop();
                    }
                })
            ;

            RegisterType.HostControl.Define(this)
                .WithFlag(0, name: "bus reset",
                        valueProviderCallback: _ => false, // it's a lie - normally BUS reset should take 50ms after which it should go down
                        writeCallback: (_, val) =>
                        {
                            if(val)
                            {
                                busirq.Value = true;
                                UpdateInterrupts();
                            }
                        })
            ;

            RegisterType.HostTransfer.Define(this)
                .WithValueField(0, 4, out var ep, name: "ep")
                .WithFlag(4, out var setup, name: "setup")
                .WithFlag(5, out var outnin, name: "outnin")
                .WithFlag(6, name: "iso")
                .WithFlag(7, out var hs, name: "hs")
                .WithWriteCallback((_, v) =>
                {
                    var device = this.ChildCollection.Values.FirstOrDefault(x => x.USBCore.Address == deviceAddress.Value);
                    if(device == null)
                    {
                        this.Log(LogLevel.Warning, "Tried to send setup packet to a device with address 0x{0:X}, but it's not connected", deviceAddress.Value);

                        // setting the IRQ is necessary to allow communication right after the usb device address has changed
                        hxfrdnirq.Value = true;
                        UpdateInterrupts();

                        return;
                    }

                    if(setup.Value)
                    {
                        this.Log(LogLevel.Noisy, "Setup TX");
                        if(ep.Value != 0)
                        {
                            this.Log(LogLevel.Error, "This model does not support SETUP packets on EP different than 0");
                            return;
                        }

                        HandleSetup(device);
                    }
                    else if(hs.Value)
                    {
                        this.Log(LogLevel.Noisy, "Handshake {0}", outnin.Value ? "out" : "in");

                        hxfrdnirq.Value = true;
                        UpdateInterrupts();
                    }
                    else
                    {
                        USBEndpoint endpoint = null;
                        if(ep.Value != 0)
                        {
                            endpoint = device.USBCore.GetEndpoint((int)ep.Value);
                            if(endpoint == null)
                            {
                                this.Log(LogLevel.Error, "Tried to access a non-existing EP #{0}", ep.Value);

                                hxfrdnirq.Value = true;
                                UpdateInterrupts();
                                return;
                            }
                        }

                        if(outnin.Value)
                        {
                            this.Log(LogLevel.Noisy, "Bulk out");
                            HandleBulkOut(endpoint);
                        }
                        else
                        {
                            this.Log(LogLevel.Noisy, "Bulk in");
                            HandleBulkIn(endpoint);
                        }
                    }
                })
            ;

            RegisterType.PeripheralAddress.Define(this)
                .WithValueField(0, 8, out deviceAddress, name: "address")
            ;

            RegisterType.SetupFifo.Define(this)
                .WithValueField(0, 8, name: "setup data", valueProviderCallback: _ =>
                {
                    if(setupQueue.Count == 0)
                    {
                        this.Log(LogLevel.Warning, "Trying to read from an empty setup queue");
                        return 0;
                    }
                    return setupQueue.Dequeue();

                },
                writeCallback: (_, val) =>
                {
                    setupQueue.Enqueue((byte)val);
                    if(setupQueue.Count > 8)
                    {
                        this.Log(LogLevel.Warning, "Too much data put in the setup queue. Initial bytes will be dropped");
                        setupQueue.Dequeue();
                    }
                })
            ;

            RegisterType.ReceiveFifo.Define(this)
                .WithValueField(0, 8, FieldMode.Read, name: "data", valueProviderCallback: _ =>
                {
                    if(receiveQueue.Count == 0)
                    {
                        this.Log(LogLevel.Warning, "Trying to read from an empty receive queue");
                        return 0;
                    }
                    return receiveQueue.Dequeue();
                })
            ;

            RegisterType.ReceiveQueueLength.Define(this)
                .WithValueField(0, 7, FieldMode.Read, name: "count", valueProviderCallback: _ => (uint)receiveQueue.Count)
                .WithReservedBits(7, 1)
            ;

            RegisterType.SendQueueLength.Define(this)
                .WithValueField(0, 7, out sendByteCount, name: "count")
                .WithReservedBits(7, 1)
                .WithWriteCallback((_, __) =>
                {
                    sndbavirq.Value = false;
                    UpdateInterrupts();
                })
            ;

            RegisterType.SendFifo.Define(this)
                .WithValueField(0, 8, name: "data",
                    valueProviderCallback: _ =>
                    {
                        if(sendQueue.Count == 0)
                        {
                            this.Log(LogLevel.Warning, "Trying to read from an empty send queue");
                            return 0;
                        }
                        return sendQueue.Dequeue();

                    },
                    writeCallback: (_, val) =>
                    {
                        sendQueue.Enqueue((byte)val);
                        if(sendQueue.Count > FifoSize)
                        {
                            this.Log(LogLevel.Warning, "Too much data put in the send queue. Initial bytes will be dropped");
                            sendQueue.Dequeue();
                        }
                    })
            ;
        }

        private void HandleCommandByte(byte data)
        {
            var dir = (CommandDirection)((data >> 1) & 0x1);
            lastRegister = (RegisterType)(data >> 3);

            this.Log(LogLevel.Noisy, "Command byte detected: operation: {0}, register: {1}", dir, lastRegister);

            switch(dir)
            {
                case CommandDirection.Write:
                    state = State.Writing;
                    break;

                case CommandDirection.Read:
                    state = State.Reading;
                    break;

                default:
                    throw new ArgumentException("Unsupported command direction");
            }
        }

        private void BumpFrameNumber()
        {
            this.Log(LogLevel.Noisy, "Bumping frame number");

            frameirq.Value = true;
            UpdateInterrupts();
        }

        private void HandleBulkOut(USBEndpoint endpoint)
        {
            if(endpoint != null)
            {
                if(sendByteCount.Value != sendQueue.Count)
                {
                    this.Log(LogLevel.Warning, "Requested to send BULK out {0} bytes of data, but there are {1} bytes in the queue.", sendByteCount.Value, sendQueue.Count);
                }

                var bytesToSend = sendQueue.DequeueRange((int)sendByteCount.Value);
                this.Log(LogLevel.Noisy, "Writing {0} bytes to the device", bytesToSend.Length);
                endpoint.WriteData(bytesToSend);

                sndbavirq.Value = true;
            }

            hxfrdnirq.Value = true;
            UpdateInterrupts();
        }

        private void HandleBulkIn(USBEndpoint endpoint)
        {
            if(endpoint != null)
            {
                this.Log(LogLevel.Noisy, "Initiated read from the device");
                endpoint.SetDataReadCallbackOneShot((_, data) =>
                {
                    this.Log(LogLevel.Noisy, "Received data from the device");
#if DEBUG_PACKETS
                    this.Log(LogLevel.Noisy, Misc.PrettyPrintCollectionHex(data));
#endif
                    EnqueueReceiveData(data);

                    hxfrdnirq.Value = true;
                    UpdateInterrupts();
                });
            }
            else
            {
                hxfrdnirq.Value = true;
                UpdateInterrupts();
            }
        }

        private void HandleSetup(IUSBDevice device)
        {
            if(!Packet.TryDecode<SetupPacket>(setupQueue.DequeueAll(), out var setupPacket))
            {
                this.Log(LogLevel.Error, "Could not decode SETUP packet - some data might be lost!");
                return;
            }

            device.USBCore.HandleSetupPacket(setupPacket, response =>
            {
                EnqueueReceiveData(response);

                hxfrdnirq.Value = true;
                UpdateInterrupts();
            });
        }

        private void EnqueueReceiveData(IEnumerable<byte> data)
        {
            if(receiveQueue.EnqueueRange(data) > 0)
            {
                rcvdavirq.Value = true;
            }
        }

        private RegisterType lastRegister;
        private State state;

        private IFlagRegisterField condetirq;
        private IFlagRegisterField condetie;
        private IFlagRegisterField ie;
        private IFlagRegisterField kStatus;
        private IFlagRegisterField jStatus;
        private IFlagRegisterField busirq;
        private IFlagRegisterField busie;
        private IFlagRegisterField frameirq;
        private IFlagRegisterField frameie;
        private IFlagRegisterField hxfrdnie;
        private IFlagRegisterField hxfrdnirq;
        private IFlagRegisterField rcvdaie;
        private IFlagRegisterField rcvdavirq;
        private IValueRegisterField deviceAddress;
        private IValueRegisterField sendByteCount;
        private IFlagRegisterField sndbavie;
        private IFlagRegisterField sndbavirq;

        private readonly Queue<byte> setupQueue;
        private readonly Queue<byte> receiveQueue;
        private readonly Queue<byte> sendQueue;
        private readonly IManagedThread bumper;

        private const byte ChipRevision = 0x13;
        private const int BumpsPerSecond = 1;
        private const int FifoSize = 64;

        private enum State
        {
            Idle,
            Writing,
            Reading
        }

        private enum CommandDirection
        {
            Read = 0,
            Write = 1
        }

        private enum RegisterType
        {
            ReceiveFifo = 1,
            SendFifo = 2,
            SetupFifo = 4,

            ReceiveQueueLength = 6,
            SendQueueLength = 7,

            USBIrqPending = 13,
            USBIrqEnabled = 14,

            USBControl = 15,
            CPUControl = 16,

            PinControl = 17,
            Revision = 18,

            HostIrqPending = 25,
            HostIrqEnabled = 26,

            Mode = 27,
            PeripheralAddress = 28,
            HostControl = 29,
            HostTransfer = 30,
            HostResult = 31
        }
    }
}
