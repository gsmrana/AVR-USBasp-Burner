using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using Megamind.IO.FileFormat;

namespace AVR_USBasp_Burner
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Enums and Const

        public const string AtmelChipXml = @"data\AtmelChip.xml";

        public enum Hardware
        {
            USBasp, //USB Custom Class
            UNO,    //STK500
            MEGA    //STK500v2
        };

        public enum MemType
        {
            Flash,
            EEPROM,
            ALL
        }

        public enum Operation
        {
            Detect,
            Read,
            Verify,
            Erase,
            Write
        }

        public readonly string[] IspClock = new string[]
        {
            "AUTO",
            "500 Hz",
            "1 kHz",
            "2 kHz",
            "4 kHz",
            "8 kHz",
            "16 kHz",
            "32 kHz",
            "93.75 kHz",
            "187.5 kHz",
            "375 kHz",
            "750 kHz",
            "1.5 MHz",
        };

        public readonly int[] StkBaudrate = new int[]
        {
            19200,
            38400,
            57600,
            115200
        };

        #endregion

        #region Data

        Hardware _hardware = Hardware.USBasp;
        Programmer _programmer = new USBasp();
        readonly ChipDb _chipDb = new ChipDb();

        //memory buffer
        IntelHex _flashBuffer = new IntelHex();
        IntelHex _eepromBuffer = new IntelHex();

        //ui config buffer 
        string[] _chipNames;
        string[] _serialPorts;
        Chip _selectedChip;
        MemType _selectedMemory = MemType.Flash;

        Operation _operation;

        bool _readWriteOnProgress;
        public bool ReadWriteOnProgress
        {
            get
            {
                return _readWriteOnProgress;
            }
            set
            {
                _readWriteOnProgress = value;
                Dispatcher.Invoke(new Action(() =>
                {
                    ToolBar1.IsEnabled = !_readWriteOnProgress;
                    MenuBar1.IsEnabled = !_readWriteOnProgress;
                }));
            }
        }

        #endregion

        #region ctor

        public MainWindow()
        {
            InitializeComponent();

            /* load button icons */
            btnOpenImg.Source = GetThumbnail("Icons/Open.ico");
            btnSaveImg.Source = GetThumbnail("Icons/Save.ico");
            btnReadImg.Source = GetThumbnail("Icons/Read.ico");
            btnWriteImg.Source = GetThumbnail("Icons/Write.ico");
            btnVerifyImg.Source = GetThumbnail("Icons/Verify.ico");
            btnEraseImg.Source = GetThumbnail("Icons/Erase.ico");
            btnDetectImg.Source = GetThumbnail("Icons/Detect.ico");

            FlashViewer.FontFamily = new FontFamily("Consolas");
            EepromViewer.FontFamily = new FontFamily("Consolas");
            LogViewer.FontFamily = new FontFamily("Consolas");
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                /* populate supported programmer names */
                ComboBoxMethod.ItemsSource = Enum.GetNames(typeof(Hardware));
                ComboBoxMethod.SelectedIndex = 0;

                /* populate isp prog clock */
                ComboBoxProgClock.ItemsSource = IspClock;
                ComboBoxProgClock.SelectedIndex = 0;

                /* populate arduino settings */
                ComboBoxArduinoBaudrate.ItemsSource = StkBaudrate;
                ComboBoxArduinoBaudrate.SelectedIndex = StkBaudrate.Length - 1;
                TextBoxResetPulse.Text = "150";

                /* populate chip menu from xml data */
                _chipDb.Load(AtmelChipXml);
                _selectedChip = _chipDb.Chips.FirstOrDefault(c => c.Name == "ATmega328P");
                _chipNames = _chipDb.Chips.Select(c => c.Name).ToArray();
                Array.Sort(_chipNames);
                MenuChip.Items.Clear();
                foreach (var item in _chipNames)
                {
                    var menuitem = new MenuItem
                    {
                        Header = item,
                        IsCheckable = true
                    };
                    menuitem.Click += MenuSelectChip_Click;
                    if (item == _selectedChip.Name) menuitem.IsChecked = true;
                    MenuChip.Items.Add(menuitem);
                }
            }
            catch (Exception ex)
            {
                PopupError(ex.Message);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            DisconnectHardware();
        }

        #endregion

        #region Helper Methods

        private ImageSource GetThumbnail(string fileName)
        {
            if (!File.Exists(fileName)) return new BitmapImage();

            var buffer = File.ReadAllBytes(fileName);
            var memoryStream = new MemoryStream(buffer);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.DecodePixelWidth = 64;
            bitmap.DecodePixelHeight = 64;
            bitmap.StreamSource = memoryStream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private void UpdateFlashBufferDisplay(string str)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                if (str == "") FlashViewer.Document.Blocks.Clear();
                else
                {
                    FlashViewer.AppendText(str);
                    TabControl1.SelectedIndex = 0;
                }
            }));
        }

        private void UpdateEepromBufferDisplay(string str)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                if (str == "") EepromViewer.Document.Blocks.Clear();
                else
                {
                    EepromViewer.AppendText(str);
                    TabControl1.SelectedIndex = 1;
                }
            }));
        }

        private void AppendLog(string str, bool newline = true)
        {
            if (newline) str += "\r";
            Dispatcher.Invoke(new Action(() =>
            {
                LogViewer.AppendText(str);
                LogViewer.ScrollToEnd();
            }));
        }

        private void UpdateProgressBar(int percent, string message = "")
        {
            Dispatcher.Invoke(new Action(() =>
            {
                ProgressBarReadWrite.Value = percent;
                if (message != "") tbStatus.Text = message;
            }));
        }

        private void UpdateStatusBarItem(string str, int item)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                if (item == 0) tbStatus.Text = str;
                else if (item == 1) tbChip.Text = str;
                else if (item == 2) tbMethod.Text = str;
                else if (item == 3) tbPort.Text = str;
                else if (item == 4) tbBaud.Text = str;
                else MessageBox.Show(str, "Info");
            }));
        }

        private void PopupError(string message, string caption = "Error")
        {
            var icon = MessageBoxImage.Error;
            if (caption == "Warning") icon = MessageBoxImage.Warning;
            else if (caption == "Info") icon = MessageBoxImage.Information;
            Dispatcher.Invoke(new Action(() =>
            {
                MessageBox.Show(message, caption, MessageBoxButton.OK, icon);
            }));
        }

        public void ReadFuseFile(string filename)
        {
            var lines = File.ReadAllLines(filename);
            var lfuse = lines[0].Split(':')[1].Trim();
            var hfuse = lines[1].Split(':')[1].Trim();
            var efuse = lines[2].Split(':')[1].Trim();
            TextBoxLowFuse.Text = lfuse;
            TextBoxHighFuse.Text = hfuse;
            TextBoxExFuse.Text = efuse;
            TabControl1.SelectedIndex = 2;
        }

        public void WriteFuseFile(string filename)
        {
            var lines = new List<string>
            {
                "LOW FUSE : " + TextBoxLowFuse.Text,
                "HIGH FUSE : " + TextBoxHighFuse.Text,
                "EXTENDED FUSE : " + TextBoxExFuse.Text
            };
            File.WriteAllLines(filename, lines);
        }

        public static string ByteArrayToHexString(byte[] bytes, string separator = "")
        {
            return BitConverter.ToString(bytes).Replace("-", separator);
        }

        public static byte[] HexStringToByteArray(string hexstr)
        {
            hexstr = hexstr.Replace("-", "");
            hexstr = hexstr.Replace(" ", "");
            return Enumerable.Range(0, hexstr.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hexstr.Substring(x, 2), 16))
                             .ToArray();
        }

        #endregion

        #region Read Write Progress Events

        int _prevRWProgress = 0;
        int _expectedRWbytes = 0;
        private void Memory_OnReadProgress(object sender, int bytes)
        {
            try
            {
                var progress = (bytes * 100) / _expectedRWbytes;
                if (progress != _prevRWProgress)
                {
                    UpdateProgressBar(progress, "Reading..." + progress + "%");
                    _prevRWProgress = progress;
                }
            }
            catch (Exception) { }
        }

        private void Memory_OnWriteProgress(object sender, int bytes)
        {
            try
            {
                var progress = (bytes * 100) / _expectedRWbytes;
                if (progress != _prevRWProgress)
                {
                    UpdateProgressBar(progress, "Writing..." + progress + " %");
                    _prevRWProgress = progress;
                }
            }
            catch (Exception) { }
        }

        #endregion

        #region Menu Events

        private void MenuSelectMemory_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in MenuSelectMemory.Items)
            {
                if (item is MenuItem menu)
                    menu.IsChecked = false;
            }
            var mi = sender as MenuItem;
            mi.IsChecked = true;
            var menuheader = mi.Header.ToString();
            if (menuheader == "Flash")
            {
                _selectedMemory = MemType.Flash;
                TabControl1.SelectedIndex = 0;
            }
            else if (menuheader == "EEPROM")
            {
                _selectedMemory = MemType.EEPROM;
                TabControl1.SelectedIndex = 1;
            }
            else if (menuheader == "All") _selectedMemory = MemType.ALL;
        }

        private void MenuSelectChip_Click(object sender, RoutedEventArgs e)
        {
            foreach (MenuItem item in MenuChip.Items)
                item.IsChecked = false;

            var mi = sender as MenuItem;
            mi.IsChecked = true;

            _selectedChip = _chipDb.Chips.FirstOrDefault(c => c.Name == mi.Header.ToString());
            UpdateStatusBarItem(_selectedChip.Name, 1);
        }

        private void MenuGetSync_Click(object sender, RoutedEventArgs e)
        {
            if (!(_programmer is STK500)) return;

            Task.Run(new Action(() =>
            {
                try
                {
                    AppendLog(string.Format("Connecting {0}...", _programmer.GetType().Name), false);
                    _programmer.Open();
                    AppendLog("OK");

                    AppendLog("STK Get Sync...", false);
                    _programmer.GetSync();
                    AppendLog("OK");
                    DisconnectHardware();
                }
                catch (Exception ex)
                {
                    DisconnectHardware();
                    AppendLog("FAILED");
                    PopupError(ex.Message);
                }
            }));
        }

        private void MenuStartUserApp_Click(object sender, RoutedEventArgs e)
        {
            if (!(_programmer is STK500)) return;

            Task.Run(new Action(() =>
            {
                try
                {
                    AppendLog(string.Format("Connecting {0}...", _programmer.GetType().Name), false);
                    _programmer.Open();
                    AppendLog("OK");

                    AppendLog("STK Start User App...", false);
                    _programmer.StartUserApp();
                    AppendLog("OK");
                    DisconnectHardware();
                }
                catch (Exception ex)
                {
                    DisconnectHardware();
                    AppendLog("FAILED");
                    PopupError(ex.Message);
                }
            }));
        }

        private void MenuChipManager_Click(object sender, RoutedEventArgs e)
        {

        }

        private void MenuUpdate_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("https://github.com/gsmrana/AVR-USBasp-Burner");
        }

        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            var aboutbox = new AboutBox();
            aboutbox.ShowDialog();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        #endregion

        #region Tab Control/Drag and Drop Events

        private void TabControl1_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_selectedMemory == MemType.ALL) return;
                var index = TabControl1.SelectedIndex;
                if (index >= 0 && index <= 1)
                    MenuSelectMemory_Click(MenuSelectMemory.Items[index], null);
            }
            catch (Exception ex)
            {
                AppendLog("FAILED");
                PopupError(ex.Message);
            }
        }

        private void TabControl1_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
        }

        private void TabControl1_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    ReadFileToBuffer(files[0]);
                }
            }
            catch (Exception ex)
            {
                AppendLog("FAILED");
                PopupError(ex.Message);
            }
        }

        #endregion

        #region ToolBar Events (File Opeartion)

        private void ReloadFileToBuffer()
        {
            try
            {
                if (!string.IsNullOrEmpty(_flashBuffer.Source))
                {
                    AppendLog("Reloading Flash Buffer...", false);
                    _flashBuffer.Read();
                    UpdateFlashBufferDisplay("");
                    UpdateFlashBufferDisplay(ByteArrayToHexString(_flashBuffer.RawData));
                    AppendLog("OK");
                }

                if (!string.IsNullOrEmpty(_eepromBuffer.Source))
                {
                    AppendLog("Reloading EEPROM Buffer...", false);
                    _eepromBuffer.Read();
                    UpdateEepromBufferDisplay("");
                    UpdateEepromBufferDisplay(ByteArrayToHexString(_eepromBuffer.RawData));
                    AppendLog("OK");
                }
            }
            catch (Exception ex)
            {
                AppendLog("FAILED");
                PopupError(ex.Message);
            }
        }

        private void ReadFileToBuffer(string filename)
        {
            var ext = Path.GetExtension(filename);
            this.Title = "GSM AVR Burner - " + Path.GetFileName(filename);
            if (ext == ".hex")
            {
                _flashBuffer = new IntelHex();
                _flashBuffer.Read(filename);
                AppendLog("Flash Buffer loaded..." + _flashBuffer.DataLength + " bytes");
                UpdateFlashBufferDisplay("");
                UpdateFlashBufferDisplay(ByteArrayToHexString(_flashBuffer.RawData));
            }
            else if (ext == ".eep")
            {
                _eepromBuffer = new IntelHex();
                _eepromBuffer.Read(filename);
                AppendLog("EEPROM Buffer loaded..." + _eepromBuffer.DataLength + " bytes");
                UpdateEepromBufferDisplay("");
                UpdateEepromBufferDisplay(ByteArrayToHexString(_eepromBuffer.RawData));
            }
            else if (ext == ".fuse")
            {
                ReadFuseFile(filename);
            }
            else PopupError("Format Not Supported!");
        }

        private void ButtonOpen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ofd = new OpenFileDialog
                {
                    Filter = "IntelHex File (*.hex;*.eep;*.fuse;*.elf)|*.hex;*.eep;*.fuse;*.elf|All files (*.*)|*.*"
                };
                if (ofd.ShowDialog().Value) ReadFileToBuffer(ofd.FileName);
            }
            catch (Exception ex)
            {
                AppendLog("FAILED");
                PopupError(ex.Message);
            }
        }

        private void ButtonSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sfd = new SaveFileDialog();
                if (TabControl1.SelectedIndex == 1) sfd.Filter = "EEPROM Files (*.eep)|*.eep|" + "All files (*.*)|*.*";
                else if (TabControl1.SelectedIndex == 2) sfd.Filter = "FUSE Files (*.fuse)|*.fuse|" + "All files (*.*)|*.*";
                else sfd.Filter = "Hex Files (*.hex)|*.hex|" + "All files (*.*)|*.*";

                sfd.FileName = _selectedChip.Name;
                if (sfd.ShowDialog() == true)
                {
                    var ext = Path.GetExtension(sfd.FileName);
                    if (ext == ".hex") _flashBuffer.SaveIntelHex(sfd.FileName);
                    else if (ext == ".eep") _eepromBuffer.SaveIntelHex(sfd.FileName);
                    else if (ext == ".fuse") WriteFuseFile(sfd.FileName);
                    AppendLog(Path.GetFileName(sfd.FileName) + " File Saved.");
                }
            }
            catch (Exception ex)
            {
                AppendLog("FAILED");
                PopupError(ex.Message);
            }
        }

        private void ButtonSaveAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sfd = new SaveFileDialog
                {
                    Filter = "All files (*.*)|*.*",
                    FileName = _selectedChip.Name
                };
                if (sfd.ShowDialog() == true)
                {
                    _flashBuffer.SaveIntelHex(sfd.FileName + ".hex");
                    AppendLog(Path.GetFileName(sfd.FileName + ".hex") + " File Saved.");
                    _eepromBuffer.SaveIntelHex(sfd.FileName + ".eep");
                    AppendLog(Path.GetFileName(sfd.FileName + ".eep") + " File Saved.");
                    WriteFuseFile(sfd.FileName + ".fuse");
                    AppendLog(Path.GetFileName(sfd.FileName + ".fuse") + " File Saved.");
                }
            }
            catch (Exception ex)
            {
                AppendLog("FAILED");
                PopupError(ex.Message);
            }
        }

        #endregion

        #region ComboBox Events

        private void InitProgrammerObject(string portName, int baudRate)
        {
            if (_hardware == Hardware.USBasp) _programmer = new USBasp();
            else if (_hardware == Hardware.UNO) _programmer = new STK500(portName, baudRate);
            else if (_hardware == Hardware.MEGA) _programmer = new STK500V2(portName, baudRate);
            _programmer.OnReadProgress += Memory_OnReadProgress;
            _programmer.OnWriteProgress += Memory_OnWriteProgress;
        }

        private void ComboBoxMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var portName = "";
                var baudRate = 0;
                _hardware = (Hardware)ComboBoxMethod.SelectedIndex;

                if (_hardware == Hardware.USBasp)
                {
                    portName = "USB";
                    ComboBoxPortName.Text = "";
                    ComboBoxPortName.IsEnabled = false;
                }
                else
                {
                    ComboBoxPortName.IsEnabled = true;
                    var ports = SerialPort.GetPortNames();
                    Array.Sort(ports);
                    ComboBoxPortName.ItemsSource = _serialPorts = ports;
                    if (_serialPorts.Length > 0)
                    {
                        ComboBoxPortName.SelectedIndex = _serialPorts.Length - 1;
                        portName = _serialPorts[ComboBoxPortName.SelectedIndex];
                    }
                    baudRate = int.Parse(ComboBoxArduinoBaudrate.Text);
                }
                InitProgrammerObject(portName, baudRate);

                UpdateStatusBarItem(_hardware.ToString(), 2);
                UpdateStatusBarItem(portName, 3);
                UpdateStatusBarItem(baudRate > 0 ? baudRate.ToString() : "", 4);
            }
            catch (Exception ex)
            {
                PopupError(ex.Message);
            }
        }

        private void ComboBoxPortName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var index = ComboBoxPortName.SelectedIndex;
                if (index < 0) return;
                var portName = _serialPorts[index];
                var baudRate = int.Parse(ComboBoxArduinoBaudrate.Text);
                InitProgrammerObject(portName, baudRate);
                UpdateStatusBarItem(portName, 3);
            }
            catch (Exception ex)
            {
                PopupError(ex.Message);
            }
        }

        private void ComboBoxPortName_DropDownOpened(object sender, EventArgs e)
        {
            try
            {
                var ports = SerialPort.GetPortNames();
                Array.Sort(ports);
                ComboBoxPortName.ItemsSource = _serialPorts = ports;
            }
            catch (Exception ex)
            {
                PopupError(ex.Message);
            }
        }

        #endregion		

        #region Chip Opearion Thread

        private bool ConnectVerifySignature()
        {
            ReadWriteOnProgress = true;

            AppendLog(string.Format("Connecting {0}...", _programmer.GetType().Name), false);
            _programmer.Open();
            AppendLog("OK");

            AppendLog("Reading Signature...", false);
            var signature = _programmer.ReadSignature();
            AppendLog(string.Format("0x{0:X6}", signature));
            if (signature == _selectedChip.Signature) return true;

            var detectedChip = _chipDb.Chips.FirstOrDefault(c => c.Signature == signature);
            if (detectedChip == null)
            {
                PopupError("Chip signature not found in xml database!", "Warning");
                return false;
            }

            var result = false;
            AppendLog("Detected Chip..." + detectedChip.Name);
            Dispatcher.Invoke(new Action(() =>
            {
                var str = string.Format("Signature does not match with selected chip!" +
                            "\n\nDetected chip: {0}\nDo you want to continue?", detectedChip.Name);
                var userInput = MessageBox.Show(str, "Warning", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (userInput == MessageBoxResult.Yes)
                {
                    var chipIndex = Array.IndexOf(_chipNames, detectedChip.Name);
                    MenuSelectChip_Click(MenuChip.Items[chipIndex], null);
                    result = true;
                }
            }));
            return result;
        }

        private void DisconnectHardware()
        {
            try
            {
                if (_programmer.IsConnected)
                    _programmer.Close();
            }
            catch (Exception ex)
            {
                PopupError(ex.Message);
            }
            ReadWriteOnProgress = false;
        }

        private void StartOperationThread()
        {
            Task.Run(new Action(() =>
            {
                try
                {
                    if (!ConnectVerifySignature())
                    {
                        DisconnectHardware();
                        return;
                    }

                    if (_operation == Operation.Erase)
                    {
                        AppendLog("Erasing Chip...", false);
                        _programmer.ChipErase();
                        AppendLog("OK");
                    }

                    if (_operation == Operation.Read)
                    {
                        if (_selectedMemory == MemType.Flash || _selectedMemory == MemType.ALL)
                        {
                            UpdateFlashBufferDisplay("");
                            _expectedRWbytes = _selectedChip.FlashSize;
                            AppendLog("Reading Flash...", false);
                            var memBlock = new MemoryBlock
                            {
                                Data = _programmer.ReadMemory(MemSource.Flash, 0, _selectedChip.FlashSize)
                            };
                            _flashBuffer = new IntelHex();
                            _flashBuffer.MemBlocks.Add(memBlock);
                            AppendLog(_flashBuffer.DataLength + " bytes OK");
                            UpdateFlashBufferDisplay(ByteArrayToHexString(_flashBuffer.RawData));
                        }

                        if (_selectedMemory == MemType.EEPROM || _selectedMemory == MemType.ALL)
                        {
                            UpdateEepromBufferDisplay("");
                            _expectedRWbytes = _selectedChip.EepromSize;
                            AppendLog("Reading EEPROM...", false);
                            var memBlock = new MemoryBlock
                            {
                                Data = _programmer.ReadMemory(MemSource.EEPROM, 0, _selectedChip.EepromSize)
                            };
                            _eepromBuffer = new IntelHex();
                            _eepromBuffer.MemBlocks.Add(memBlock);
                            AppendLog(_eepromBuffer.DataLength + " bytes OK");
                            UpdateEepromBufferDisplay(ByteArrayToHexString(_eepromBuffer.RawData));
                        }
                    }

                    if (_operation == Operation.Verify)
                    {
                        if (_selectedMemory == MemType.Flash || _selectedMemory == MemType.ALL)
                        {
                            _expectedRWbytes = _selectedChip.FlashSize;
                            AppendLog("Reading Flash...", false);
                            var verifyBuffer = _programmer.ReadMemory(MemSource.Flash, 0, _selectedChip.FlashSize);
                            AppendLog(verifyBuffer.Length + " bytes OK");

                            AppendLog("Verifying Flash...", false);
                            var failIndex = -1;
                            var flashData = _flashBuffer.RawData;
                            for (int i = 0; i < flashData.Length; i++)
                            {
                                if (flashData[i] != verifyBuffer[i])
                                {
                                    failIndex = i;
                                    break;
                                }
                            }
                            if (failIndex >= 0)
                            {
                                AppendLog("Mismatch at index " + failIndex);
                                PopupError("Flash Verification Mismatch!");
                            }
                            else AppendLog("OK");
                        }

                        if (_selectedMemory == MemType.EEPROM || _selectedMemory == MemType.ALL)
                        {
                            _expectedRWbytes = _selectedChip.EepromSize;
                            AppendLog("Reading EEPROM...", false);
                            var verifyBuffer = _programmer.ReadMemory(MemSource.EEPROM, 0, _selectedChip.EepromSize);
                            AppendLog(verifyBuffer.Length + " bytes OK");

                            AppendLog("Verifying EEPROM...", false);
                            var failIndex = -1;
                            var eepData = _eepromBuffer.RawData;
                            for (int i = 0; i < eepData.Length; i++)
                            {
                                if (eepData[i] != verifyBuffer[i])
                                {
                                    failIndex = i;
                                    break;
                                }
                            }

                            if (failIndex >= 0)
                            {
                                AppendLog("Mismatch at index " + failIndex);
                                PopupError("EEPROM Verification Mismatch!");
                            }
                            else AppendLog("OK");
                        }
                    }

                    if (_operation == Operation.Write)
                    {
                        if (_selectedMemory == MemType.Flash || _selectedMemory == MemType.ALL)
                        {
                            if (_programmer is USBasp)
                            {
                                AppendLog("Erasing Chip...", false);
                                _programmer.ChipErase();
                                AppendLog("OK");
                            }

                            var blockNo = 1;
                            AppendLog("Writing Flash...", false);
                            foreach (var item in _flashBuffer.MemBlocks)
                            {
                                if (_flashBuffer.MemBlocks.Count > 0) AppendLog("\rWriting MemBlock..." + blockNo++);
                                _expectedRWbytes = item.Data.Length;
                                _programmer.WriteMemory(MemSource.Flash, item.Start, _selectedChip.PageSize, item.Data);
                            }
                            AppendLog(_flashBuffer.DataLength + " bytes OK");
                        }

                        if (_selectedMemory == MemType.EEPROM || _selectedMemory == MemType.ALL)
                        {
                            var blockNo = 1;
                            AppendLog("Writing EEPROM...", false);
                            foreach (var item in _eepromBuffer.MemBlocks)
                            {
                                if (_flashBuffer.MemBlocks.Count > 0) AppendLog("\rWriting MemBlock..." + blockNo++);
                                _expectedRWbytes = item.Data.Length;
                                _programmer.WriteMemory(MemSource.EEPROM, item.Start, _selectedChip.PageSize, item.Data);
                            }
                            AppendLog(_eepromBuffer.DataLength + " bytes OK");
                        }
                    }

                    DisconnectHardware();
                }
                catch (Exception ex)
                {
                    DisconnectHardware();
                    AppendLog("FAILED");
                    PopupError(ex.Message);
                }
            }));
        }

        #endregion

        #region ToolBar Events (Chip Opeartion)

        private void ButtonRead_Click(object sender, RoutedEventArgs e)
        {
            this.Title = "GSM AVR Burner - New Read";
            _operation = Operation.Read;
            StartOperationThread();
        }

        private void ButtonWrite_Click(object sender, RoutedEventArgs e)
        {
            ReloadFileToBuffer();
            _operation = Operation.Write;
            StartOperationThread();
        }

        private void ButtonVerify_Click(object sender, RoutedEventArgs e)
        {
            _operation = Operation.Verify;
            StartOperationThread();
        }

        private void ButtonErase_Click(object sender, RoutedEventArgs e)
        {
            var str = "Are you sure want to erase the entire chip?";
            var userInput = MessageBox.Show(str, "Warning", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (userInput != MessageBoxResult.Yes) return;

            _operation = Operation.Erase;
            StartOperationThread();
        }

        private void ButtonDetect_Click(object sender, RoutedEventArgs e)
        {
            _operation = Operation.Detect;
            StartOperationThread();
        }

        #endregion

        #region Fuse Bits Tab Events

        private void CheckBoxWriteHfuse_Click(object sender, RoutedEventArgs e)
        {
            var writeHfuse = CheckBoxWriteHfuse.IsChecked.Value;
            TextBoxHighFuse.IsReadOnly = !writeHfuse;
            if (writeHfuse) TextBoxHighFuse.Focus();
        }

        private void CheckBoxWriteLfuse_Click(object sender, RoutedEventArgs e)
        {
            var writeLfuse = CheckBoxWriteLfuse.IsChecked.Value;
            TextBoxLowFuse.IsReadOnly = !writeLfuse;
            if (writeLfuse) TextBoxLowFuse.Focus();
        }

        private void CheckBoxWriteEfuse_Click(object sender, RoutedEventArgs e)
        {
            var writeEfuse = CheckBoxWriteEfuse.IsChecked.Value;
            TextBoxExFuse.IsReadOnly = !writeEfuse;
            if (writeEfuse) TextBoxExFuse.Focus();
        }

        private void BtnReadAllFuse_Click(object sender, RoutedEventArgs e)
        {
            if (ReadWriteOnProgress) return;
            if (!(_programmer is USBasp)) return;

            Task.Run(new Action(() =>
            {
                try
                {
                    if (ConnectVerifySignature())
                    {
                        AppendLog("Reading Fuse Bits...", false);
                        var lfuse = _programmer.ReadLowFuse();
                        var hfuse = _programmer.ReadHighFuse();
                        var efuse = _programmer.ReadExFuse();
                        AppendLog("OK");
                        DisconnectHardware();

                        Dispatcher.Invoke(new Action(() =>
                        {
                            TextBoxLowFuse.Text = lfuse.ToString("X2");
                            TextBoxHighFuse.Text = hfuse.ToString("X2");
                            TextBoxExFuse.Text = efuse.ToString("X2");
                        }));
                    }
                }
                catch (Exception ex)
                {
                    DisconnectHardware();
                    AppendLog("FAILED");
                    PopupError(ex.Message);
                }
            }));
        }

        private void BtnWriteAllFuse_Click(object sender, RoutedEventArgs e)
        {
            if (ReadWriteOnProgress) return;
            if (!(_programmer is USBasp)) return;

            var writeLfuse = CheckBoxWriteLfuse.IsChecked.Value;
            var writeHfuse = CheckBoxWriteHfuse.IsChecked.Value;
            var writeEfuse = CheckBoxWriteEfuse.IsChecked.Value;
            if (!writeLfuse && !writeHfuse && !writeEfuse) return;
            byte hfuse = 0, lfuse = 0, efuse = 0;

            try
            {
                lfuse = byte.Parse(TextBoxLowFuse.Text, NumberStyles.HexNumber);
                hfuse = byte.Parse(TextBoxHighFuse.Text, NumberStyles.HexNumber);
                efuse = byte.Parse(TextBoxExFuse.Text, NumberStyles.HexNumber);
            }
            catch (Exception ex)
            {
                PopupError(ex.Message);
                return;
            }

            Task.Run(new Action(() =>
            {
                try
                {
                    if (ConnectVerifySignature())
                    {
                        AppendLog("Writing Fuse Bits...", false);
                        if (writeLfuse)
                        {
                            _programmer.WriteLowFuse(lfuse);
                            Thread.Sleep(100);
                        }
                        if (writeHfuse)
                        {
                            _programmer.WriteHighFuse(hfuse);
                            Thread.Sleep(100);
                        }
                        if (writeEfuse)
                        {
                            _programmer.WriteExFuse(efuse);
                            Thread.Sleep(100);
                        }
                        AppendLog("OK");
                        DisconnectHardware();
                    }
                }
                catch (Exception ex)
                {
                    DisconnectHardware();
                    AppendLog("FAILED");
                    PopupError(ex.Message);
                }
            }));
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri));
            e.Handled = true;
        }

        #endregion

        #region Lock Bits Tab Events

        private void CheckBoxWriteLockBits_Click(object sender, RoutedEventArgs e)
        {
            var writeLockBits = CheckBoxWriteLockBits.IsChecked.Value;
            TextBoxLockBits.IsReadOnly = !writeLockBits;
            if (writeLockBits) TextBoxLockBits.Focus();
        }

        private void BtnReadAllLockBits_Click(object sender, RoutedEventArgs e)
        {
            if (ReadWriteOnProgress) return;
            if (!(_programmer is USBasp)) return;

            Task.Run(new Action(() =>
            {
                try
                {
                    if (ConnectVerifySignature())
                    {
                        AppendLog("Reading Fuse Bits...", false);
                        var calib = _programmer.ReadCalibrationByte();
                        var lockbits = _programmer.ReadLockBits();
                        AppendLog("OK");
                        DisconnectHardware();

                        Dispatcher.Invoke(new Action(() =>
                        {
                            TextBoxCalibByte.Text = calib.ToString("X2");
                            TextBoxLockBits.Text = lockbits.ToString("X2");
                        }));
                    }
                }
                catch (Exception ex)
                {
                    DisconnectHardware();
                    AppendLog("FAILED");
                    PopupError(ex.Message);
                }
            }));
        }

        private void BtnWriteAllLockBits_Click(object sender, RoutedEventArgs e)
        {
            if (ReadWriteOnProgress) return;
            if (!(_programmer is USBasp)) return;

            var writeLockBits = CheckBoxWriteLockBits.IsChecked.Value;
            if (!writeLockBits) return;
            byte lockbits = 0;

            try
            {
                lockbits = byte.Parse(TextBoxLockBits.Text, NumberStyles.HexNumber);
            }
            catch (Exception ex)
            {
                PopupError(ex.Message);
                return;
            }

            Task.Run(new Action(() =>
            {
                try
                {
                    if (ConnectVerifySignature())
                    {
                        AppendLog("Writing Fuse Bits...", false);
                        if (writeLockBits) _programmer.WriteLockBits(lockbits);
                        AppendLog("OK");
                        DisconnectHardware();
                    }
                }
                catch (Exception ex)
                {
                    DisconnectHardware();
                    AppendLog("FAILED");
                    PopupError(ex.Message);
                }
            }));
        }

        #endregion

        #region Settings Tab Events

        private void ButtonSetProgClock_Click(object sender, RoutedEventArgs e)
        {
            if (ReadWriteOnProgress) return;
            if (!(_programmer is USBasp)) return;

            byte progclk = 0;
            try
            {
                progclk = (byte)ComboBoxProgClock.SelectedIndex;
            }
            catch (Exception ex)
            {
                PopupError(ex.Message);
                return;
            }

            Task.Run(new Action(() =>
            {
                try
                {
                    if (_programmer is USBasp programmer)
                    {
                        AppendLog(string.Format("Connecting {0}...", _programmer.GetType().Name), false);
                        programmer.Open(false);
                        AppendLog("OK");

                        AppendLog("Setting Prog Clock...", false);
                        programmer.SetProgrammingSCK(progclk);
                        AppendLog("OK");
                        DisconnectHardware();
                    }
                }
                catch (Exception ex)
                {
                    DisconnectHardware();
                    AppendLog("FAILED");
                    PopupError(ex.Message);
                }
            }));
        }

        private void ButtonSetArduinoParams_Click(object sender, RoutedEventArgs e)
        {
            if (ReadWriteOnProgress) return;

            try
            {
                if (_programmer is STK500 || _programmer is STK500V2)
                {
                    AppendLog("Setting Arduino Params...", false);
                    var portName = _serialPorts[ComboBoxPortName.SelectedIndex];
                    var baudRate = int.Parse(ComboBoxArduinoBaudrate.Text);
                    InitProgrammerObject(portName, baudRate);
                    _programmer.ResetPulseTime = int.Parse(TextBoxResetPulse.Text);
                    UpdateStatusBarItem(baudRate > 0 ? baudRate.ToString() : "", 4);
                    AppendLog("OK");
                }
            }
            catch (Exception ex)
            {
                AppendLog("FAILED");
                PopupError(ex.Message);
            }
        }

        #endregion
    }
}
