using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVR_USBasp_Burner
{
    public enum MemSource { Flash, EEPROM }

    public abstract class Programmer
    {
        #region Properties and Events

        public bool IsConnected { get; protected set; }

        public delegate void ProgressEvent(object sender, int bytes);
        public event ProgressEvent OnReadProgress;
        public event ProgressEvent OnWriteProgress;

        #endregion

        #region Property for STK500/STK500V2

        public int ResetPulseTime { get; set; } //in ms
        public int ReadWriteTimeout { get; set; } //in ms

        #endregion

        #region Open Close

        public abstract void Open(bool enterProgMode = true);
        public abstract void Close();

        #endregion

        #region Trigger Events

        protected virtual void NotifyReadProgress(int bytes)
        {
            OnReadProgress?.Invoke(this, bytes);
        }

        protected virtual void NotifyWriteProgress(int bytes)
        {
            OnWriteProgress?.Invoke(this, bytes);
        }

        #endregion

        #region Read Write Flash Eeprom Memory

        public virtual byte[] ReadMemory(MemSource memType, int startAddress, int maxSize, bool breakOnEraseBlock = false)
        {
            var flashbuf = new byte[maxSize];
            int bytesReaded = 0;
            int bytesToRead = maxSize;
            int blockSize = 200;

            while (bytesToRead > 0)
            {
                if (bytesToRead < blockSize) blockSize = bytesToRead;

                LoadAddress(startAddress);
                var blockbuf = new byte[blockSize];
                ReadPage(memType, blockSize, ref blockbuf);
                if (breakOnEraseBlock && blockbuf.All(v => v == 0xFF)) break; //erased block found
                Array.Copy(blockbuf, 0, flashbuf, bytesReaded, blockSize);

                bytesReaded += blockSize;
                startAddress += blockSize;
                bytesToRead -= blockSize;
                NotifyReadProgress(bytesReaded);
            }

            Array.Resize(ref flashbuf, bytesReaded);
            return flashbuf;
        }

        public virtual void WriteMemory(MemSource memType, int startAddress, int pageSize, byte[] databuf)
        {
            int bytesWritten = 0;
            int bytesToWrite = databuf.Length;
            int blockSize = pageSize;

            while (bytesToWrite > 0)
            {
                if (bytesToWrite < blockSize) blockSize = bytesToWrite;

                LoadAddress(startAddress);
                var blockbuf = new byte[blockSize];
                Array.Copy(databuf, bytesWritten, blockbuf, 0, blockSize);
                WritePage(memType, blockSize, blockbuf);

                bytesWritten += blockSize;
                startAddress += blockSize;
                bytesToWrite -= blockSize;
                NotifyWriteProgress(bytesWritten);
            }
        }

        #endregion

        #region Read Write Page

        protected abstract void LoadAddress(int address);
        protected abstract void ReadPage(MemSource source, int size, ref byte[] data);
        protected abstract void WritePage(MemSource source, int size, byte[] data);

        #endregion

        #region Misc

        public virtual string GetDeviceName()
        {
            throw new NotImplementedException();
        }

        public virtual int GetPageSize()
        {
            throw new NotImplementedException();
        }

        public virtual void GetSync()
        {
            throw new NotImplementedException();
        }

        public virtual void StartUserApp()
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Get Chip Properties     

        public virtual void ChipErase()
        {
            throw new NotImplementedException();
        }

        public virtual int ReadSignature()
        {
            throw new NotImplementedException();
        }

        public virtual byte ReadCalibrationByte()
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Get Set Chip Config

        public virtual byte ReadLowFuse()
        {
            throw new NotImplementedException();
        }

        public virtual byte ReadHighFuse()
        {
            throw new NotImplementedException();
        }

        public virtual byte ReadExFuse()
        {
            throw new NotImplementedException();
        }

        public virtual byte ReadLockBits()
        {
            throw new NotImplementedException();
        }

        public virtual void WriteLowFuse(byte val)
        {
            throw new NotImplementedException();
        }

        public virtual void WriteHighFuse(byte val)
        {
            throw new NotImplementedException();
        }

        public virtual void WriteExFuse(byte val)
        {
            throw new NotImplementedException();
        }

        public virtual void WriteLockBits(byte val)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Static Methods

        protected static byte MSB16(int val)
        {
            return (byte)((val >> 8) & 0xFF);
        }

        protected static byte LSB16(int val)
        {
            return (byte)(val & 0xFF);
        }

        #endregion
    }
}
