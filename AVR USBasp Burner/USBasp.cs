using System;
using System.Threading;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace AVR_USBasp_Burner
{
    public class USBasp : Programmer
    {
        #region Constant

        // USBasp device identifiers
        public const int USBASP_VID = 0x16C0;  // VOTI
        public const int USBASP_PID = 0x05DC;  // Obdev's free shared PID

        // USBasp config and interface
        public const byte LibUsbConfig = 1;
        public const int LibUsbInterface = 0;

        #endregion

        #region Data

        public int VendorId;
        public int ProductId;

        UsbDevice _usbdevice;
        byte[] _rxbuffer = new byte[8];

        #endregion

        #region ctor

        public USBasp(int vid = USBASP_VID, int pid = USBASP_PID)
        {
            VendorId = vid;
            ProductId = pid;
        }

        public override void Open(bool enterProgMode = true)
        {
            // find and open the usb device.
            var devinfo = new UsbDeviceFinder(VendorId, ProductId);

            // auto detect LibUsbDevice or WinUsbDevice device
            if (_usbdevice != null) IsConnected = _usbdevice.Close();
            _usbdevice = UsbDevice.OpenUsbDevice(devinfo);

            if (_usbdevice == null)
            {
                throw new Exception(string.Format("USBasp not connected!\nError Code: {0} \n\n{1}",
                    UsbDevice.LastErrorNumber, UsbDevice.LastErrorString));
            }

            // If this is a "whole" usb device (libusb-win32, linux libusb)
            // it will have an IUsbDevice interface. If not (WinUSB) the
            // variable will be null indicating this is an interface of a device
            if (_usbdevice is IUsbDevice libusbdev)
            {
                // This is a "whole" USB device. Before it can be used,
                // the desired configuration and interface must be selected.
                libusbdev.SetConfiguration(LibUsbConfig);
                libusbdev.ClaimInterface(LibUsbInterface);
            }

            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_CONNECT, ref _rxbuffer);

            if (enterProgMode)
            {
                ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_ENABLEPROG, ref _rxbuffer);
                if (_rxbuffer[0] != ASPConst.USBASP_ENTER_PROG_MODE_OK)
                {
                    Close();
                    throw new Exception("USBasp Error: Failed to enter programming mode!");
                }
            }

            IsConnected = true;
        }

        public override void Close()
        {
            if (_usbdevice.IsOpen)
            {
                ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_DISCONNECT, ref _rxbuffer);

                // If this is a "whole" usb device (libusb-win32, linux libusb-1.0)
                // it exposes an IUsbDevice interface. If not (WinUSB) the
                // 'wholeUsbDevice' variable will be null indicating this is
                // an interface of a device; it does not require or support
                // configuration and interface selection.
                if (_usbdevice is IUsbDevice libusbdev)
                {
                    libusbdev.ReleaseInterface(LibUsbInterface);
                }

                _usbdevice.Close();
                IsConnected = false;
            }

            // free usb resources
            UsbDevice.Exit();
        }

        #endregion

        #region Read/Write USB EndPoint

        private void ReadUsbEndpointIn(byte request, ref byte[] datain, int prog_address = 0, int prog_pagesize = 0, int prog_nbytes = 0)
        {
            byte reqType = (byte)UsbRequestType.TypeVendor | (byte)UsbEndpointDirection.EndpointIn;
            var packet = new UsbSetupPacket(reqType, request, (short)prog_address, (short)prog_pagesize, (short)prog_nbytes);
            var issuccess = _usbdevice.ControlTransfer(ref packet, datain, datain.Length, out _);

            if (!issuccess)
            {
                var str = string.Format("Failed to ControlTransfer USB UsbEndpointIn!\nError Code: {0} \n\n{1}",
                    UsbDevice.LastErrorNumber, UsbDevice.LastErrorString);
                throw new Exception(str);
            }
        }

        private void WriteUsbEndpointOut(byte request, byte[] outdata, int prog_address = 0, int prog_pagesize = 0, int prog_nbytes = 0)
        {
            byte reqType = (byte)UsbRequestType.TypeVendor | (byte)UsbEndpointDirection.EndpointOut;
            var packet = new UsbSetupPacket(reqType, request, (short)prog_address, (short)prog_pagesize, (short)prog_nbytes);
            var issuccess = _usbdevice.ControlTransfer(ref packet, outdata, outdata.Length, out _);

            if (!issuccess)
            {
                var str = string.Format("Failed to ControlTransfer USB UsbEndpointOut!\nError Code: {0} \n\n{1}",
                    UsbDevice.LastErrorNumber, UsbDevice.LastErrorString);
                throw new Exception(str);
            }
        }

        #endregion

        #region Read Write Flash EEPROM Memory

        int _pageAddress = 0;

        protected override void LoadAddress(int address)
        {
            _pageAddress = address;
        }

        protected override void ReadPage(MemSource source, int size, ref byte[] data)
        {
            var cmd = (source == MemSource.Flash) ? ASPCMD.USBASP_FUNC_READFLASH : ASPCMD.USBASP_FUNC_READEEPROM;
            ReadUsbEndpointIn(cmd, ref data, _pageAddress, 0, size);
        }

        protected override void WritePage(MemSource source, int size, byte[] data)
        {
            var cmd = (source == MemSource.Flash) ? ASPCMD.USBASP_FUNC_WRITEFLASH : ASPCMD.USBASP_FUNC_WRITEEEPROM;
            WriteUsbEndpointOut(cmd, data, _pageAddress, 0, size); //eeprom only
        }

        public override void WriteMemory(MemSource memType, int startAddress, int pageSize, byte[] databuf)
        {
            if (memType == MemSource.EEPROM)
            {
                base.WriteMemory(memType, startAddress, pageSize, databuf);
                return;
            }

            int bytesWritten = 0;
            int bytesToWrite = databuf.Length;
            int blockSize = ASPConst.USBASP_RW_BLOCKSIZE;
            int blockFlag = ASPConst.USBASP_BLOCKFLAG_FIRST;

            while (bytesToWrite > 0)
            {
                if (bytesToWrite <= blockSize)
                {
                    blockSize = bytesToWrite;
                    blockFlag |= ASPConst.USBASP_BLOCKFLAG_LAST;
                }

                // paged write
                var param = (pageSize & 0xFF); //lsb
                param |= ((blockFlag & 0x0F) + ((pageSize & 0xF00) >> 4)) << 8; //msb, TP: Mega128 fix
                var blockbuf = new byte[blockSize];
                Array.Copy(databuf, bytesWritten, blockbuf, 0, blockSize);
                WriteUsbEndpointOut(ASPCMD.USBASP_FUNC_WRITEFLASH, blockbuf, startAddress, param, blockSize);

                blockFlag = 0;
                bytesWritten += blockSize;
                startAddress += blockSize;
                bytesToWrite -= blockSize;
                NotifyWriteProgress(bytesWritten);
            }
        }

        #endregion

        #region Chip Opearation

        public void SetProgrammingSCK(byte prog_sck)
        {
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_SETISPSCK, ref _rxbuffer, prog_sck);
            if (_rxbuffer[0] != ASPConst.USBASP_SET_SCK_CLOCK_OK)
                throw new Exception("USBasp Error: Failed to set Programming Clock!");
        }

        public bool IsChipReady()
        {
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.READ_BSY_BIT, 0);
            return (_rxbuffer[3] > 0) ? true : false;
        }

        public override void ChipErase()
        {
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.CHIP_ERASE, 0);
            Thread.Sleep(1000);  //wait for complete erase operation
        }

        #endregion

        #region Get Chip Config

        public override int ReadSignature()
        {
            var signature = 0;

            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.READ_SIGNATURE, 0x00);
            signature |= _rxbuffer[3] << 16; //msb

            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.READ_SIGNATURE, 0x01);
            signature |= _rxbuffer[3] << 8;

            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.READ_SIGNATURE, 0x02);
            signature |= _rxbuffer[3] << 0;  //lsb

            return signature;
        }

        public override byte ReadLowFuse()
        {
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.READ_LOW_FUSE, 0);
            return _rxbuffer[3];
        }

        public override byte ReadHighFuse()
        {
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.READ_HIGH_FUSE, 0);
            return _rxbuffer[3];
        }

        public override byte ReadExFuse()
        {
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.READ_EXTEND_FUSE, 0);
            return _rxbuffer[3];
        }

        public override byte ReadLockBits()
        {
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.READ_LOCK_BITS, 0);
            return _rxbuffer[3];
        }

        public override byte ReadCalibrationByte()
        {
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.READ_CALIB_BYTE, 0);
            return _rxbuffer[3];
        }

        #endregion

        #region Set Chip Config

        public override void WriteLowFuse(byte val)
        {
            var param = val << 8;
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.WRITE_LOW_FUSE, param);
        }

        public override void WriteHighFuse(byte val)
        {
            var param = val << 8;
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.WRITE_HIGH_FUSE, param);
        }

        public override void WriteExFuse(byte val)
        {
            var param = val << 8;
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.WRITE_EXTEND_FUSE, param);
        }

        public override void WriteLockBits(byte val)
        {
            var param = val << 8;
            ReadUsbEndpointIn(ASPCMD.USBASP_FUNC_TRANSMIT, ref _rxbuffer, ISPCMD.WRITE_LOCK_BITS, param);
        }

        #endregion
    }

    #region USBasp Command and Constant

    public enum ISPSCK
    {
        /* ISP SCK speed identifiers */
        ISP_SCK_AUTO = 0,
        ISP_SCK_0_5 = 1,    /* 500 Hz */
        ISP_SCK_1 = 2,      /*   1 kHz */
        ISP_SCK_2 = 3,      /*   2 kHz */
        ISP_SCK_4 = 4,      /*   4 kHz */
        ISP_SCK_8 = 5,      /*   8 kHz */
        ISP_SCK_16 = 6,     /*  16 kHz */
        ISP_SCK_32 = 7,     /*  32 kHz */
        ISP_SCK_93_75 = 8,  /*  93.75 kHz */
        ISP_SCK_187_5 = 9,  /* 187.5  kHz */
        ISP_SCK_375 = 10,   /* 375 kHz   */
        ISP_SCK_750 = 11,   /* 750 kHz   */
        ISP_SCK_1500 = 12   /* 1.5 MHz   */
    }

    public static class ISPCMD
    {
        public const UInt16 CHIP_ERASE = 0x80AC;
        public const UInt16 READ_BSY_BIT = 0x00F0;
        public const UInt16 READ_SIGNATURE = 0x0030;
        public const UInt16 READ_CALIB_BYTE = 0x0038;

        public const UInt16 READ_LOW_FUSE = 0x0050;
        public const UInt16 READ_HIGH_FUSE = 0x0858;
        public const UInt16 READ_EXTEND_FUSE = 0x0850;
        public const UInt16 READ_LOCK_BITS = 0x0058;

        public const UInt16 WRITE_LOW_FUSE = 0xA0AC;
        public const UInt16 WRITE_HIGH_FUSE = 0xA8AC;
        public const UInt16 WRITE_EXTEND_FUSE = 0xA4AC;
        public const UInt16 WRITE_LOCK_BITS = 0xE0AC;
    }

    public static class ASPConst
    {
        /* Block mode data size */
        public const byte USBASP_RW_BLOCKSIZE = 200;

        /* USBASP capabilities */
        public const byte USBASP_CAP_0_TPI = 0x01;

        /* Block mode flags */
        public const byte USBASP_BLOCKFLAG_FIRST = 1;
        public const byte USBASP_BLOCKFLAG_LAST = 2;

        public const byte USBASP_ENTER_PROG_MODE_OK = 0;
        public const byte USBASP_SET_SCK_CLOCK_OK = 0;
    }

    public static class ASPCMD
    {
        /* USB function call identifiers */
        public const byte USBASP_FUNC_CONNECT = 1;
        public const byte USBASP_FUNC_DISCONNECT = 2;
        public const byte USBASP_FUNC_TRANSMIT = 3;
        public const byte USBASP_FUNC_READFLASH = 4;
        public const byte USBASP_FUNC_ENABLEPROG = 5;
        public const byte USBASP_FUNC_WRITEFLASH = 6;
        public const byte USBASP_FUNC_READEEPROM = 7;
        public const byte USBASP_FUNC_WRITEEEPROM = 8;
        public const byte USBASP_FUNC_SETLONGADDRESS = 9;
        public const byte USBASP_FUNC_SETISPSCK = 10;
        public const byte USBASP_FUNC_TPI_CONNECT = 11;
        public const byte USBASP_FUNC_TPI_DISCONNECT = 12;
        public const byte USBASP_FUNC_TPI_RAWREAD = 13;
        public const byte USBASP_FUNC_TPI_RAWWRITE = 14;
        public const byte USBASP_FUNC_TPI_READBLOCK = 15;
        public const byte USBASP_FUNC_TPI_WRITEBLOCK = 16;
        public const byte USBASP_FUNC_GETCAPABILITIES = 127;
    }

    public static class TPICMD
    {
        /* TPI instructions */
        public const byte TPI_OP_SLD = 0x20;
        public const byte TPI_OP_SLD_INC = 0x24;
        public const byte TPI_OP_SST = 0x60;
        public const byte TPI_OP_SST_INC = 0x64;
        //public const byte TPI_OP_SSTPR(a) = (0x68 | (a));
        //public const byte TPI_OP_SIN(a)   = (0x10 | (((a)<<1)&0x60) | ((a)&0x0F) );
        //public const byte TPI_OP_SOUT(a)  = (0x90 | (((a)<<1)&0x60) | ((a)&0x0F) );
        //public const byte TPI_OP_SLDCS(a) = (0x80 | ((a)&0x0F) );
        //public const byte TPI_OP_SSTCS(a) = (0xC0 | ((a)&0x0F) );
        public const byte TPI_OP_SKEY = 0xE0;

        /* TPI control/status registers */
        public const byte TPIIR = 0xF;
        public const byte TPIPCR = 0x2;
        public const byte TPISR = 0x0;

        // TPIPCR bits
        public const byte TPIPCR_GT_2 = 0x04;
        public const byte TPIPCR_GT_1 = 0x02;
        public const byte TPIPCR_GT_0 = 0x01;
        public const byte TPIPCR_GT_128b = 0x00;
        public const byte TPIPCR_GT_64b = 0x01;
        public const byte TPIPCR_GT_32b = 0x02;
        public const byte TPIPCR_GT_16b = 0x03;
        public const byte TPIPCR_GT_8b = 0x04;
        public const byte TPIPCR_GT_4b = 0x05;
        public const byte TPIPCR_GT_2b = 0x06;
        public const byte TPIPCR_GT_0b = 0x07;

        // TPISR bits                               
        public const byte TPISR_NVMEN = 0x02;

        /* NVM registers */
        public const byte NVMCSR = 0x32;
        public const byte NVMCMD = 0x33;

        // NVMCSR bits                             
        public const byte NVMCSR_BSY = 0x80;

        // NVMCMD values
        public const byte NVMCMD_NOP = 0x00;
        public const byte NVMCMD_CHIP_ERASE = 0x10;
        public const byte NVMCMD_SECTION_ERASE = 0x14;
        public const byte NVMCMD_WORD_WRITE = 0x1D;
    }

    #endregion

}
