using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace AVR_USBasp_Burner
{
    public class STK500V2 : Programmer, IDisposable
    {
        #region Data 

        readonly SerialPort _serialPort;

        int _expectedByte;
        bool _wait4response;
        int _rxByteCount;
        readonly byte[] _rxbuffer = new byte[4096];
        readonly object _rxBufferLock = new object();
        readonly AutoResetEvent _rxEvent = new AutoResetEvent(false);

        #endregion

        #region ctor

        public STK500V2(string portName = "COM1", int baudRate = 115200)
        {
            ResetPulseTime = 150;
            ReadWriteTimeout = 1500;
            _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
            _serialPort.DataReceived += SerialPort_DataReceived;
            _serialPort.ErrorReceived += SerialPort_ErrorReceived;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
                _serialPort.Dispose();
            _rxEvent.Dispose();
        }

        public override void Open(bool enterProgMode = true)
        {
            _serialPort.Open();

            // Reset Chip
            //_serialPort.DtrEnable = false;
            //Thread.Sleep(ResetPulseTime);
            _serialPort.DtrEnable = true;
            Thread.Sleep(ResetPulseTime);

            IsConnected = true;
        }

        public override void Close()
        {
            IsConnected = false;
            _serialPort.Close();
        }

        #endregion

        #region Read Write to SerialPort

        byte _sequenceNo = 0;

        private byte[] GeneratePacket(byte[] body)
        {
            var bodylen = body.Length;
            var pkt = new byte[bodylen + 6];
            var idx = 0;
            pkt[idx++] = Stkv2Const.MESSAGE_START; //header
            pkt[idx++] = _sequenceNo++;         //sequnce no
            pkt[idx++] = MSB16(bodylen);       //body size msb       
            pkt[idx++] = LSB16(bodylen);       //body size lsb
            pkt[idx++] = Stkv2Const.TOKEN;      //token
            Array.Copy(body, 0, pkt, idx, bodylen); //body
            byte csb = 0;
            for (int i = 0; i < (pkt.Length - 1); i++)
                csb ^= pkt[i];
            pkt[pkt.Length - 1] = csb;        //checksum
            return pkt;
        }

        private byte[] ExecuteCommand(byte[] request, int expectedByte)
        {
            var response = new byte[expectedByte];

            var txbuf = GeneratePacket(request);
            _expectedByte = expectedByte + 6; //packet overhead
            _wait4response = true;
            _rxByteCount = 0;

            //Debug.WriteLine(string.Format("TX[{0:00}]: {1}", txbuf.Length, BitConverter.ToString(txbuf).Replace("-", "")));
            _serialPort.DiscardOutBuffer();
            _serialPort.DiscardInBuffer();
            _serialPort.Write(txbuf, 0, txbuf.Length);
            var resp = _rxEvent.WaitOne(ReadWriteTimeout);
            _wait4response = false;

            if (!resp && _rxByteCount == 0)
                throw new Exception("STK No Response!");
            if (_rxbuffer[0] != Stkv2Const.MESSAGE_START)
                throw new Exception("STK Not in Sync!");
            //if (_rxbuffer[1] != Stkv2Const.STATUS_CMD_OK)
            //throw new Exception("STK Command Failed!");
            //if (_rxbuffer[expectedByte + 1] != StkCommand.STK_OK)
            //throw new Exception("STK Invalid Packet!");

            lock (_rxBufferLock)
            {
                Array.Copy(_rxbuffer, 5, response, 0, response.Length);
            }
            return response;
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (e.EventType == SerialData.Eof || !_wait4response) return;

            var bytesToRead = _serialPort.BytesToRead;
            lock (_rxBufferLock)
            {
                if (_rxByteCount + bytesToRead < _rxbuffer.Length)
                {
                    _serialPort.Read(_rxbuffer, _rxByteCount, bytesToRead);
                    _rxByteCount += bytesToRead;
                    //Debug.WriteLine(string.Format("RX[{0:00}]: {1}", _rxByteCount, BitConverter.ToString(_rxbuffer, 0, _rxByteCount).Replace("-","")));
                    if (_rxByteCount >= _expectedByte) _rxEvent.Set();
                }
            }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            Debug.WriteLine("SerialPort Error: " + e.EventType);
        }

        #endregion

        #region Chip Operation

        public override int ReadSignature()
        {
            var cmd = new byte[6];
            cmd[0] = Stkv2Command.CMD_READ_SIGNATURE_ISP;
            var signature = 0;
            for (byte i = 0; i < 3; i++)
            {
                cmd[4] = i; //sig byte selection
                var data = ExecuteCommand(cmd, 4);
                signature |= data[2] << (8 * (2 - i)); //msb first
            }
            return signature;
        }

        #endregion

        #region Extended Chip Operation

        public override string GetDeviceName()
        {
            byte[] cmd = { Stkv2Command.CMD_SIGN_ON };
            var data = ExecuteCommand(cmd, 11);
            return Encoding.ASCII.GetString(data, 3, data[2]);
        }

        #endregion

        #region Read Write Page

        protected override void LoadAddress(int address)
        {
            var cmd = new byte[5];
            address >>= 1; //divide by 2 (stk500 standard)
            cmd[0] = Stkv2Command.CMD_LOAD_ADDRESS;
            cmd[3] = MSB16(address);
            cmd[4] = LSB16(address);
            ExecuteCommand(cmd, 2);
        }

        protected override void ReadPage(MemSource source, int size, ref byte[] data)
        {
            var cmd = new byte[3];
            if (source == MemSource.Flash) cmd[0] = Stkv2Command.CMD_READ_FLASH_ISP;
            else cmd[0] = Stkv2Command.CMD_READ_EEPROM_ISP;
            cmd[1] = MSB16(size);
            cmd[2] = LSB16(size);
            var response = ExecuteCommand(cmd, size + 3);
            Array.Copy(response, 2, data, 0, size);
        }

        protected override void WritePage(MemSource source, int size, byte[] data)
        {
            var cmd = new byte[10 + size];
            if (source == MemSource.Flash) cmd[0] = Stkv2Command.CMD_PROGRAM_FLASH_ISP;
            else cmd[0] = Stkv2Command.CMD_PROGRAM_EEPROM_ISP;
            cmd[1] = MSB16(size);
            cmd[2] = LSB16(size);
            Array.Copy(data, 0, cmd, 10, size);
            ExecuteCommand(cmd, 2);
        }

        #endregion
    }

    #region STK500v2 Command and Const

    public static class Stkv2Const
    {
        public const byte MESSAGE_START = 0x1B; //= ESC = 27 decimal
        public const byte TOKEN = 0x0E;

        // *****************[ STK status constants ]***************************

        // Success
        public const byte STATUS_CMD_OK = 0x00;

        // Warnings
        public const byte STATUS_CMD_TOUT = 0x80;
        public const byte STATUS_RDY_BSY_TOUT = 0x81;
        public const byte STATUS_SET_PARAM_MISSING = 0x82;

        // Errors
        public const byte STATUS_CMD_FAILED = 0xC0;
        public const byte STATUS_CKSUM_ERROR = 0xC1;
        public const byte STATUS_CMD_UNKNOWN = 0xC9;

        // *****************[ STK answer constants ]***************************

        public const byte ANSWER_CKSUM_ERROR = 0xB0;
    }

    public static class Stkv2Command
    {
        // *****************[ STK general command constants ]**************************

        public const byte CMD_SIGN_ON = 0x01;
        public const byte CMD_SET_PARAMETER = 0x02;
        public const byte CMD_GET_PARAMETER = 0x03;
        public const byte CMD_SET_DEVICE_PARAMETERS = 0x04;
        public const byte CMD_OSCCAL = 0x05;
        public const byte CMD_LOAD_ADDRESS = 0x06;
        public const byte CMD_FIRMWARE_UPGRADE = 0x07;

        // *****************[ STK ISP command constants ]******************************

        public const byte CMD_ENTER_PROGMODE_ISP = 0x10;
        public const byte CMD_LEAVE_PROGMODE_ISP = 0x11;
        public const byte CMD_CHIP_ERASE_ISP = 0x12;
        public const byte CMD_PROGRAM_FLASH_ISP = 0x13;
        public const byte CMD_READ_FLASH_ISP = 0x14;
        public const byte CMD_PROGRAM_EEPROM_ISP = 0x15;
        public const byte CMD_READ_EEPROM_ISP = 0x16;
        public const byte CMD_PROGRAM_FUSE_ISP = 0x17;
        public const byte CMD_READ_FUSE_ISP = 0x18;
        public const byte CMD_PROGRAM_LOCK_ISP = 0x19;
        public const byte CMD_READ_LOCK_ISP = 0x1A;
        public const byte CMD_READ_SIGNATURE_ISP = 0x1B;
        public const byte CMD_READ_OSCCAL_ISP = 0x1C;
        public const byte CMD_SPI_MULTI = 0x1D;

        // *****************[ STK PP command constants ]*******************************

        public const byte CMD_ENTER_PROGMODE_PP = 0x20;
        public const byte CMD_LEAVE_PROGMODE_PP = 0x21;
        public const byte CMD_CHIP_ERASE_PP = 0x22;
        public const byte CMD_PROGRAM_FLASH_PP = 0x23;
        public const byte CMD_READ_FLASH_PP = 0x24;
        public const byte CMD_PROGRAM_EEPROM_PP = 0x25;
        public const byte CMD_READ_EEPROM_PP = 0x26;
        public const byte CMD_PROGRAM_FUSE_PP = 0x27;
        public const byte CMD_READ_FUSE_PP = 0x28;
        public const byte CMD_PROGRAM_LOCK_PP = 0x29;
        public const byte CMD_READ_LOCK_PP = 0x2A;
        public const byte CMD_READ_SIGNATURE_PP = 0x2B;
        public const byte CMD_READ_OSCCAL_PP = 0x2C;

        public const byte CMD_SET_CONTROL_STACK = 0x2D;

        // *****************[ STK HVSP command constants ]*****************************

        public const byte CMD_ENTER_PROGMODE_HVSP = 0x30;
        public const byte CMD_LEAVE_PROGMODE_HVSP = 0x31;
        public const byte CMD_CHIP_ERASE_HVSP = 0x32;
        public const byte CMD_PROGRAM_FLASH_HVSP = 0x33;
        public const byte CMD_READ_FLASH_HVSP = 0x34;
        public const byte CMD_PROGRAM_EEPROM_HVSP = 0x35;
        public const byte CMD_READ_EEPROM_HVSP = 0x36;
        public const byte CMD_PROGRAM_FUSE_HVSP = 0x37;
        public const byte CMD_READ_FUSE_HVSP = 0x38;
        public const byte CMD_PROGRAM_LOCK_HVSP = 0x39;
        public const byte CMD_READ_LOCK_HVSP = 0x3A;
        public const byte CMD_READ_SIGNATURE_HVSP = 0x3B;
        public const byte CMD_READ_OSCCAL_HVSP = 0x3C;

        // *****************[ STK parameter constants ]***************************

        public const byte PARAM_BUILD_NUMBER_LOW = 0x80;
        public const byte PARAM_BUILD_NUMBER_HIGH = 0x81;
        public const byte PARAM_HW_VER = 0x90;
        public const byte PARAM_SW_MAJOR = 0x91;
        public const byte PARAM_SW_MINOR = 0x92;
        public const byte PARAM_VTARGET = 0x94;
        public const byte PARAM_VADJUST = 0x95;
        public const byte PARAM_OSC_PSCALE = 0x96;
        public const byte PARAM_OSC_CMATCH = 0x97;
        public const byte PARAM_SCK_DURATION = 0x98;
        public const byte PARAM_TOPCARD_DETECT = 0x9A;
        public const byte PARAM_STATUS = 0x9C;
        public const byte PARAM_DATA = 0x9D;
        public const byte PARAM_RESET_POLARITY = 0x9E;
        public const byte PARAM_CONTROLLER_INIT = 0x9F;
    }


    #endregion

}
