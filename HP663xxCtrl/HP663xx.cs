﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ivi.Visa.Interop;

namespace HP663xxCtrl
{
    public class HP663xx
    {
        CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;

        ResourceManager rm = new ResourceManager();
        FormattedIO488 dev = new FormattedIO488();
        public bool HasDVM { get; private set; }
        public bool HasOutput2 { get; private set; }

        string ID;
        public void Reset()
        {
            dev.WriteString("*RST");
            dev.WriteString("*CLS");
            dev.WriteString("STAT:PRES");
            dev.WriteString("*SRE 0");
            dev.WriteString("*ESE 0");
        }
        public enum CurrentRanges {
            TWENTY_mA,
            MEDIUM,
            HIGH
        };
        public void SetCurrentRange(CurrentRanges range) {
            switch (range)
            {
                case CurrentRanges.TWENTY_mA: dev.WriteString("SENS:CURR:RANG MIN"); break;
                case CurrentRanges.MEDIUM: dev.WriteString("SENS:CURR:RANG 0.9"); break;
                case CurrentRanges.HIGH: dev.WriteString("SENS:CURR:RANG MAX"); break;
            }
        }
        [Flags]
        public enum OperationStatusEnum
        {
            Calibration = 1,
            WaitingForTrigger = 32,
            CV = 256,
            CV2 = 512,
            CCPositive = 1024,
            CCNegative = 2048,
            CC2 = 4096
        }
        [Flags]
        public enum QuestionableStatusEnum
        {
            OV = 1,
            OCP = 2,
            FP_Local = 8, // frontpanel local was pressed
            OverTemperature = 16,
            OpenSenseLead = 32,
            Unregulated2 = 256,
            RemoteInhibit = 512,
            Unregulated = 1024,
            OverCurrent2 = 4096,
            MeasurementOverload = 16384
        }
        [Flags]
        public enum StatusByteEnum
        {
            QuestionableStatusSummary = 8,
            MesasgeAvailable = 16,
            EventSTB = 32,
            MasterStatusSummary = 64,
            OperationStatusSummary = 128
        }
        public enum CurrentDetectorEnum {
            DC,
            ACDC
        }
        public struct StatusFlags
        {
            public QuestionableStatusEnum Questionable;
            public OperationStatusEnum Operation;

        }
        public struct InstrumentState {
            public StatusFlags Flags;
            public double IRange;
            public double V, I, V2, I2, DVM;
            public double duration;
            public bool OutputEnabled;
            public bool OutputEnabled2;
            public bool OVP;
            public bool OCP;
        }
        public struct ProgramDetails {
            public bool Enabled;
            public bool OCP;
            public bool OVP;
            public double OVPVal;
            public double V1, I1, V2, I2;

            // And other things that are not actually used during programming
            public bool HasDVM, HasOutput2;
            public string ID;
            public double MaxV1, MaxI1, MaxV2, MaxI2;
            public CurrentRanges Range;
            public CurrentDetectorEnum Detector;
        }
        public ProgramDetails ReadProgramDetails() {

            string response = Query("OUTP?;VOLT?;CURR?;"
                + ":VOLT:PROT:STAT?;:VOLT:PROT?;:CURR:PROT:STAT?" +
                (HasOutput2? ";:VOLT2?;CURR2?":"")).Trim();
            string[] parts = response.Split(new char[] { ';' });
            ProgramDetails details = new ProgramDetails() {
                Enabled = (parts[0] == "1"),
                V1 = double.Parse(parts[1],CI),
                I1 = double.Parse(parts[2],CI),
                OVP = (parts[3] == "1"),
                OVPVal = double.Parse(parts[4],CI),
                OCP = (parts[5] == "1"),
                V2 = HasOutput2? double.Parse(parts[6],CI):double.NaN,
                I2 = HasOutput2 ? double.Parse(parts[7],CI) : double.NaN,
                HasDVM = HasDVM,
                HasOutput2 = HasOutput2,
                ID = ID
            };
            // Maximums
            parts = Query("VOLT? MAX; CURR? MAX").Trim().Split(new char[] {';'});
            details.MaxV1 = double.Parse(parts[0],CI);
            details.MaxI1 = double.Parse(parts[1],CI);
            if (HasOutput2) {
                parts = Query("VOLT2? MAX; CURR2? MAX").Trim().Split(new char[] { ';' });
                details.MaxV2 = double.Parse(parts[0],CI);
                details.MaxI2 = double.Parse(parts[1],CI);

            }
            double range = Double.Parse(Query(":sense:curr:range?").Trim(),CI);
            if (range < 0.03)
                details.Range = CurrentRanges.TWENTY_mA;
            else if (range < 1.1)
                details.Range = CurrentRanges.MEDIUM;
            else
                details.Range = CurrentRanges.HIGH;

            string detector = Query("SENSE:CURR:DET?").Trim();
            switch (detector) {
                case "DC": details.Detector = CurrentDetectorEnum.DC; break;
                case "ACDC": details.Detector = CurrentDetectorEnum.ACDC; break;
                default: throw new Exception();
            }
            return details;
        }
        public InstrumentState ReadState(bool measureCh2=true, bool measureDVM=true) {
            InstrumentState ret = new InstrumentState();
            DateTime start = DateTime.Now;
            // ~23 ms
            string statusStr = Query("stat:oper:cond?;:stat:ques:cond?;:sense:curr:range?;" +
                ":OUTP1?;VOLTage:PROTection:STAT?;:CURR:PROT:STAT?").Trim();
            string[] statuses = statusStr.Split(new char[] { ';' });
            ret.Flags = new StatusFlags();
            ret.Flags.Operation = (OperationStatusEnum)int.Parse(statuses[0],CI);
            ret.Flags.Questionable = (QuestionableStatusEnum)int.Parse(statuses[1],CI);
            ret.IRange = double.Parse(statuses[2],CI);
            ret.OutputEnabled = statuses[3] == "1";
            ret.OVP = statuses[4] == "1";
            ret.OCP = statuses[5] == "1";
            // Must measure each thing individually
            // Default is 2048 points, with 46.8us rate
            // This is 95.8 ms; about 6 PLC in America, or 5 in other places.
            // But, might be better to do one PLC?
            // For CH1:
            // Setting  time
            //      1    30
            //  2048/46.8    230
            //   4096    168
            // 
            dev.WriteString("TRIG:ACQ:SOUR INT;COUNT:VOLT 1;:TRIG:ACQ:COUNT:CURR 1");
            dev.WriteString("SENS:SWE:POIN 2048; TINT 46.8e-6");
            dev.WriteString("SENS:SWE:OFFS:POIN 0;:SENS:WIND HANN");
            // Channel is about 30 ms
            ret.V = Double.Parse(Query("MEAS:VOLT?"),CI);
            ret.I = Double.Parse(Query("MEAS:CURR?"),CI);
            // Ch2 is about 100 ms
            if (measureCh2 && HasOutput2) {
                ret.V2 = Double.Parse(Query("MEAS:VOLT2?"),CI);
                ret.I2 = Double.Parse(Query("MEAS:CURR2?"),CI); // Fixed at 2048*(15.6us)
            } else {
                ret.V2 = double.NaN;
                ret.I2 = double.NaN;
            }

            // RMS is also available using MEAS:DVM:ACDC
            if(measureDVM && HasDVM)
                ret.DVM = Double.Parse(Query("MEAS:DVM?"),CI); // 2048*(15.6us) => 50 ms
            else
                ret.DVM = Double.NaN;
            ret.duration = DateTime.Now.Subtract(start).TotalMilliseconds;
            return ret;
        }
        public StatusFlags GetStatusFlags()
        {
            StatusFlags flags = new StatusFlags();
            string val = Query("stat:oper:cond?;:stat:ques:cond?");
            int[] statuses = val.Split(new char[] { ';' }).Select(x => int.Parse(x,CI)).ToArray();
            flags.Operation = (OperationStatusEnum)statuses[0];
            flags.Questionable = (QuestionableStatusEnum)statuses[1];
            return flags;
        }
        public OperationStatusEnum GetOperationStatus()
        {
            return (OperationStatusEnum)int.Parse(Query("STAT:OPER:COND?"),CI);
        }
        public QuestionableStatusEnum GetQuestionableStatus()
        {
            return (QuestionableStatusEnum)int.Parse(Query("STAT:QUES:COND?"),CI);
        }
        public enum SenseModeEnum {
            CURRENT,
            VOLTAGE,
            DVM
        }
        string Query(string cmd)
        {
            dev.WriteString(cmd);
            return dev.ReadString();
        }
        public void ClearErrors()
        {
            string msg;
            while(!( (msg = Query("SYSTem:ERRor?")).StartsWith("+0,"))) {
            }
        }
        public struct MeasArray
        {
            public SenseModeEnum Mode;
            public double TimeInterval;
            public double[][] Data;
        }
        public enum TriggerSlopeEnum {
            Immediate,
            Positive,
            Negative,
            Either
        }
        public void SetupLogging(
            SenseModeEnum mode
            ) {
            int numPoints = 4096;
            double interval = 15.6e-6;
            string modeString;
            int triggerOffset = 0;

            if (mode == SenseModeEnum.DVM && !HasDVM)
                throw new Exception();
            switch (mode) {
                case SenseModeEnum.CURRENT: modeString = "CURR"; break;
                case SenseModeEnum.VOLTAGE: modeString = "VOLT"; break;
                case SenseModeEnum.DVM: modeString = "DVM"; break;
                default: throw new InvalidOperationException("Unknown transient measurement mode");
            }
            // Immediate always has a trigger count of 1
            dev.WriteString("SENSe:FUNCtion \"" + modeString + "\"");
            dev.WriteString("SENSe:SWEEP:POINTS " + numPoints.ToString(CI) + "; " +
                "TINTerval " + interval.ToString(CI) + ";" +
                "OFFSET:POINTS " + triggerOffset.ToString(CI));
            dev.WriteString("TRIG:ACQ:SOURCE BUS");
            dev.WriteString("ABORT;*WAI");
            //dev.WriteString("INIT:NAME ACQ;:TRIG:ACQ");

            Query("*OPC?");
        }
        public struct LoggerDatapoint {
            public double Min, Mean, Max, RMS;
            public DateTime time;
        }
        public LoggerDatapoint MeasureLoggingPoint( SenseModeEnum mode) {
            LoggerDatapoint ret = new LoggerDatapoint();
            string rsp;
            string[] parts;
            switch(mode) {
                case SenseModeEnum.CURRENT:
                    rsp = Query("MEAS:CURR?;:FETCH:CURR:MIN?;MAX?;ACDC?").Trim();
                    parts = rsp.Split(new char[] { ';' });
                    ret.Mean = double.Parse(parts[0], CI);
                    ret.Min = double.Parse(parts[1], CI);
                    ret.Max = double.Parse(parts[2], CI);
                    ret.RMS = double.Parse(parts[3], CI);
                    break;
                case SenseModeEnum.VOLTAGE:
                    rsp = Query("MEAS:VOLT?;:FETCH:VOLT:MIN?;MAX?;ACDC?").Trim();
                    parts = rsp.Split(new char[] { ';' });
                    ret.Mean = double.Parse(parts[0], CI);
                    ret.Min = double.Parse(parts[1], CI);
                    ret.Max = double.Parse(parts[2],CI);
                    ret.RMS = double.Parse(parts[3],CI);
                    break;
                case SenseModeEnum.DVM:
                    rsp = Query("MEAS:DVM?").Trim();
                    parts = rsp.Split(new char[] { ';' });
                    ret.Mean = double.Parse(parts[0], CI);
                    break;
            }
            ret.time = DateTime.Now;
            return ret;
        }
        public void StartTransientMeasurement(
            SenseModeEnum mode,
            int numPoints = 4096,
            double interval = 15.6e-6,
            double level = double.NaN,
            double hysteresis = 0.0,
            int triggerCount = 1,
            TriggerSlopeEnum triggerEdge = TriggerSlopeEnum.Positive,
            int triggerOffset = 0
            )
        {
            if (triggerCount * numPoints > 4096) {
                throw new InvalidOperationException();
            }
            string modeString;
            if (mode == SenseModeEnum.DVM && !HasDVM)
                throw new Exception();
            switch (mode)
            {
                case SenseModeEnum.CURRENT: modeString = "CURR"; break;
                case SenseModeEnum.VOLTAGE:  modeString = "VOLT";  break;
                case SenseModeEnum.DVM:  modeString = "DVM"; break;
                default: throw new InvalidOperationException("Unknown transient measurement mode");
            }
            dev.WriteString("SENSe:FUNCtion \"" + modeString + "\"");
            if (numPoints < 1 || numPoints > 4096)
                throw new InvalidOperationException("Number of points must be betweer 1 and 4096");
            // Immediate always has a trigger count of 1
            if (triggerEdge == TriggerSlopeEnum.Immediate)
                triggerCount = 1;
            if (interval < 15.6e-6)
                interval = 15.6e-6;
            if (interval > 1e4)
                interval = 1e4;
            dev.WriteString("SENSe:SWEEP:POINTS " + numPoints.ToString(CI) + "; " +
                "TINTerval " + interval.ToString(CI) + ";" +
                "OFFSET:POINTS " + triggerOffset.ToString(CI));
            if(triggerEdge== TriggerSlopeEnum.Immediate || double.IsNaN(level)) {
                dev.WriteString("TRIG:ACQ:SOURCE BUS");
                dev.WriteString("ABORT;*WAI");
                dev.WriteString("INIT:NAME ACQ;:TRIG:ACQ");
            } else {
                string slopeStr = "EITH";
                switch (triggerEdge) {
                    case TriggerSlopeEnum.Either: slopeStr = "EITH"; break;
                    case TriggerSlopeEnum.Positive: slopeStr = "POS"; break;
                    case TriggerSlopeEnum.Negative: slopeStr = "NEG"; break;
                }
                dev.WriteString("TRIG:ACQ:COUNT:" + modeString + " " + triggerCount.ToString(CI) + ";" +
                    ":TRIG:ACQ:LEVEL:" + modeString + " " + level.ToString(CI) + ";" +
                    ":TRIG:ACQ:SLOPE:" + modeString + " " + slopeStr + ";" +
                    ":TRIG:ACQ:HYST:" + modeString + " " + hysteresis.ToString(CI));
                dev.WriteString("TRIG:ACQ:SOURCE INT");
                dev.WriteString("ABORT;*WAI");
                dev.WriteString("INIT:NAME ACQ");
            }
            // Clear status byte
            Query("*ESR?");
            dev.WriteString("*OPC");
        }
        public bool IsMeasurementFinished() {
            return (((int.Parse(Query("*ESR?").Trim(), CI) & 1) == 1));
        }
        public void AbortMeasurement() {
            Query("ABORT;*OPC?");
        }
        public MeasArray FinishTransientMeasurement(
            SenseModeEnum mode,
            int triggerCount = 1) {
            /*StatusByteEnum stb;
            do {
                System.Threading.Thread.Sleep(50);
                stb = (StatusByteEnum)dev.IO.ReadSTB();
            } while (!stb.HasFlag(StatusByteEnum.MesasgeAvailable));
            dev.ReadString(); // read the +1 from *OPC?*/

            if (mode == SenseModeEnum.CURRENT)
                dev.WriteString("FETCH:ARRay:CURRent?");
            else
                dev.WriteString("FETCH:ARRay:VOLTAGE?");

            float[] data = (float[])dev.ReadIEEEBlock(IEEEBinaryType.BinaryType_R4);
            MeasArray res = new MeasArray();
            res.Mode = mode;
            res.Data = new double[triggerCount][];
            int numPoints = data.Length / triggerCount;
            for (int i = 0; i < triggerCount; i++) {
                res.Data[i] = data.Skip(numPoints*i)
                    .Take(numPoints)
                    .Select(x => (double)x)
                    .ToArray();

            }
            // Might be rounded, so return the actual value, not the requested value
            res.TimeInterval = double.Parse(Query("SENSE:SWEEP:TINT?"),CI);
            return res;
        }
        public void ClearProtection() {
            dev.WriteString("OUTPut:PROTection:CLEar");
        }
        public void EnableOutput(bool enabled)
        {
            dev.WriteString("OUTPUT  " + (enabled?"ON":"OFF"));
        }
        public void SetIV(int channel, double voltage, double current) {
            dev.WriteString("VOLT" +
                (channel == 2 ? "2 " : " ") + voltage.ToString(CI) +
                ";:CURR" +
                (channel == 2 ? "2 " : " ") + current.ToString(CI) 
                );
        }
        public void SetVoltage(double voltage)
        {
            dev.WriteString("VOLT " + voltage.ToString(CI));
        }
        public void SetCurrent(double current)
        {
            dev.WriteString("CURRENT " + current.ToString(CI));
        }
        /// <summary>
        /// Set to Double.NaN to disable OVP
        /// </summary>
        /// <param name="ovp"></param>
        public void SetOVP(double ovp) {
            if (double.IsNaN(ovp))
                dev.WriteString("VOLTage:PROTection:STATe OFF");
            else {
                dev.WriteString("VOLTAGE:PROTECTION " + ovp.ToString(CI));
                dev.WriteString("VOLTage:PROTection:STATe ON");
            }
        }
        public void SetOCP(bool enabled) {
            dev.WriteString("CURR:PROT:STAT " + (enabled ? "1":"0"));
        }
        // PSC causes too much writing to non-volatile RAM. Automatically disable it, if active.
        // People _probably_ won't depend on it....
        void EnsurePSCOne()
        {
            int psc = int.Parse(Query("*PSC?"), CI);
            if (psc == 0)
                dev.WriteString("*PSC 1"); ;
        }
        public HP663xx(string addr)
        {
            dev.IO = (IMessage)rm.Open(addr, mode: AccessMode.NO_LOCK);
            dev.IO.Clear(); // clear I/O buffer
            dev.IO.Timeout = 3000; // 3 seconds

            dev.WriteString("*IDN?");
            ID = dev.ReadString();
            if (ID.Contains(",66309B,") || ID.Contains(",66319B,")) {
                HasDVM = false; HasOutput2 = true;
            } else if (ID.Contains(",66309D,") || ID.Contains(",66319D,")) {
                HasDVM = true; HasOutput2 = true;
            } else if (ID.Contains(",66311B,") || ID.Contains(",66321B,")) {
                HasDVM = false; HasOutput2 = false;
            } else if (ID.Contains(",66311D,") || ID.Contains(",66321D,")) {
                HasDVM = true; HasOutput2 = true;
            } else  {
                dev.IO.Close();
                dev.IO = null;
                throw new InvalidOperationException("Not a 66309 supply!");
            }
            dev.WriteString("STATUS:PRESET"); // Clear PTR/NTR/ENABLE register
            EnsurePSCOne();
            dev.WriteString("*CLS"); // clear status registers
            dev.WriteString("ABORT");
            ClearErrors();
            dev.WriteString("FORMAT REAL");
            dev.WriteString("FORMat:BORDer NORMAL");
            // Enable the detection of open sense leads
            dev.WriteString("SENSe:PROTection:STAT ON");
            
        }
        public void SetCurrentDetector(CurrentDetectorEnum detector) {
            switch (detector) {
                case CurrentDetectorEnum.ACDC: dev.WriteString("SENSe:CURRent:DETector ACDC"); break;
                case CurrentDetectorEnum.DC: dev.WriteString("SENSe:CURRent:DETector DC"); break;
            }
        }
        public enum OutputCompensationEnum {
            HighCap,
            LowCap
        }
        // Usually use low capacitance mode, so it's always stable. Manual says high requires C_in >5uF
        public void SetOutputCompensation(OutputCompensationEnum comp) {
            switch (comp) {
                case OutputCompensationEnum.HighCap:
                    dev.WriteString("OUTPUT:TYPE HIGH");
                    break;
                case OutputCompensationEnum.LowCap:
                    dev.WriteString("OUTPUT:TYPE LOW");
                    break;
            }
        }
        public void Close(bool goToLocal = true)
        {
            if (dev != null ) {
                if (goToLocal) {
                    IGpib gpibdev = dev.IO as IGpib;
                    if (gpibdev != null)
                        gpibdev.ControlREN(RENControlConst.GPIB_REN_GTL);
                }
                dev.IO.Close();
                try {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(dev);
                } catch { }
                dev = null;
            }
            try {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(rm);
            } catch { }
        }
    }
}
