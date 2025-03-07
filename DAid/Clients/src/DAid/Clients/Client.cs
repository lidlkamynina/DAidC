using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Net.Sockets;
using DAid.Servers;
using System.Linq;
using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DAid.Clients
{
    public class Client
    {
        string hmdpath = "C:/Users/Lietotajs/Desktop/balls/OculusIntegration_trial.exe"; // change as needed 
        string guipath = "D:/GitHub/DAidC/Clientgui3/bin/Debug/Clientgui.exe"; // change as needed, need to run once gui alone D:/GitHub/DAid/Clientgui/bin/Debug/Clientgui.exe
        string portFilePath = "D:/GitHub/DAidC/Clientgui3/bin/Debug/selected_ports.txt"; // change as needed D:/GitHub/DAid/Clientgui/bin/Debug/selected_ports.txt" 
        string questip = "192.168.8.118"; // CHANGE AS NEEDED
        private Process _hmdProcess;
        private readonly Server _server;
        private VisualizationWindow _visualizationWindow;
        private bool _isCalibrated = false;
        private bool _isVisualizing = false;

        private bool _bypassHMD = false; // New bypass flag

        // Internal sensor values (CoP)
        private double _copXLeft = 0, _copYLeft = 0;
        private double _copXRight = 0, _copYRight = 0;

        private ExerciseData _currentExercise;
        private int _currentPhase;
        
        private TcpClient _hmdClient;
        private NetworkStream _hmdStream;

        private TcpClient _guiClient;
        private NetworkStream _guiStream;
        private int currentExerciseID = 0;
        public Client(Server server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Client started. Enter commands: connect, calibrate, start, stop, hmd, gui, exit");
            OpenGUI(9999);
            _bypassHMD = false; // Bypass HMD connection for now

            while (!cancellationToken.IsCancellationRequested)
            {
                Console.Write("> ");
                string command = Console.ReadLine()?.Trim().ToLower();
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                if (command == "exit")
                {
                    Console.WriteLine("Stopping client...");
                    _server.Stop();
                    DisconnectFromHMD();
                    return;
                }
                try
                {
                    switch (command)
                    {
                        case "connect":
                            await HandleConnectCommandAsync(cancellationToken);
                            break;
                        case "calibrate":
                            HandleCalibrateCommand();
                            break;
                        case "start":
                            await HandleStartCommand();
                            break;
                        case "stop":
                            HandleStopCommand();
                            break;
                        case "exit":
                            HandleExitCommand();
                            break;
                        default:
                            Console.WriteLine("Unknown command. Valid commands: connect, calibrate, start, stop, hmd, exit.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing command '{command}': {ex.Message}");
                }
            }
        }

        private void HandleExitCommand()
        {
            Console.WriteLine("Stopping client...");
            _server.Stop();
            CloseHMD();
            DisconnectFromHMD();
            CloseGUI();
        }

        private async Task HandleConnectCommandAsync(CancellationToken cancellationToken) //retrieves port names and shows them them on gui
        {
            Console.WriteLine("[Client]: Requesting available COM ports from the server...");

            await _server.HandleConnectCommandAsync(
                cancellationToken,
                async ports =>
                {
                    string portsList = string.Join(",", ports);
                    SendMessageToGUI(portsList);
                    string filePath = portFilePath;
                    Console.WriteLine("[Client]: Waiting for selected ports file...");
                    await ListenForPortFileAsync(filePath);

                    string fileContent = File.ReadAllText(filePath).Trim();
                    if (string.IsNullOrEmpty(fileContent))
                    {
                        Console.WriteLine("[Client]: File is empty. No ports to send.");
                        return;
                    }

                    string[] portArray = fileContent.Split(',');
                    if (portArray.Length < 2)
                    {
                        Console.WriteLine("[Client]: Error - Expected two ports.");
                        return;
                    }

                    string port1 = portArray[0].Trim();
                    string port2 = portArray[1].Trim();
                    Console.WriteLine($"[Client]: Chosen ports: {port1} and {port2}");
                    _server.HandlePortResponse(port1, port2);
                });

            Console.WriteLine("[Client]: HandleConnectCommandAsync completed.");
            File.Delete(portFilePath);
        }

        private async Task ListenForPortFileAsync(string filePath)
        {
            Console.WriteLine($"[Client]: Waiting for file '{filePath}'...");
            while (!File.Exists(filePath))
                await Task.Delay(500);
            Console.WriteLine($"[Client]: File '{filePath}' detected.");
        }
        private void HandleCalibrateCommand()
        {
            if (_isCalibrated)
            {
                Console.WriteLine("Sensors are already calibrated. Use 'start' to begin visualization.");
                SendMessageToGUI("Sensors are already calibrated. Use 'start' to begin visualization.");
                return;
            }
            Console.WriteLine("Requesting server to calibrate connected devices...");
            SendMessageToGUI("Requesting server to calibrate connected devices...");
            Console.WriteLine("[Calibration]: Stand with both feet. Lift each foot one at a time after 1 second.");
            SendMessageToGUI("[Calibration]: Stand with both feet. Lift each foot one at a time after 1 second.");
            
            _server.HandleCalibrateCommand();
            _isCalibrated = true;
            Console.WriteLine("Calibration completed. Use 'start' to begin visualization.");
        }
        private async Task HandleStartCommand()
        {
            if (!_isCalibrated)
            {
                Console.WriteLine("Calibration is required before starting visualization. Use 'calibrate' first.");
                SendMessageToGUI("Calibration is required before starting visualization. Use 'calibrate' first.");
                return;
            }
            if (_isVisualizing)
            {
                Console.WriteLine("Visualization is already running.");
                SendMessageToGUI("Visualization is already running.");
                return;
            }
            _server.StartDataStream();
            OpenVisualizationWindow();
            ConnectToHMD(questip, 9003);
            SubscribeToDeviceUpdates();
            _isVisualizing = true;

            var exercises = ExerciseList.Exercises;
            var completedExerciseSets = new HashSet<int>();
            // repeating exercise groups after left and right
            var repeatSet = new Dictionary<int, List<int>>
            {
            { 2, new List<int> { 1, 2 } },  // Repeat 1 & 2 after 2
            { 8, new List<int> { 7, 8 } } // Repeat 7 & 8 after 8
            };
            Dictionary<int, int> exerciseDelays = new Dictionary<int, int> {{ 1, 1000 }, { 2, 2000 }, { 3, 2000 }  };
            for (int i = 0; i < exercises.Count; i++) {
            var exercise = exercises[3]; 
            SendExerciseConfiguration(exercise);
            
            if(exercise == exercises[6]){
                await Task.Delay(2500).ConfigureAwait(false);

            }
            if(exercise == exercises[7]){
                await Task.Delay(1000).ConfigureAwait(false);

            }
            if (exerciseDelays.TryGetValue(i, out int delay)) Thread.Sleep(delay);
            if (!completedExerciseSets.Contains(exercise.RepetitionID))
            {
                if (exercise.Intro > 0)
                    {
                    await Task.Delay(2000).ConfigureAwait(false);
                    Console.WriteLine($"[Intro]: Waiting {exercise.Intro} sec...");
                    await Task.Delay(exercise.Intro * 1000).ConfigureAwait(false);
                    }

                if (exercise.Demo > 0)
                    {
                    Console.WriteLine($"[Demo]: Showing {exercise.Demo} sec...");
                    await Task.Delay(exercise.Demo * 1000).ConfigureAwait(false);
                    }
            }
            for (int set = 0; set < exercise.Sets; set++)
                {
                if (exercise.PreparationCop > 0)
                {
                    await CheckPreparationCop(exercise.PreparationCop,exercise.LegsUsed);
                }
                Console.WriteLine($"Starting set {set + 1} of exercise {exercise.RepetitionID}");
                if (exercise.RepetitionID == 5 || exercise.RepetitionID == 6)
                {
                    await Run5and6Async(exercise).ConfigureAwait(false); //runs squats - walking lunges and lateral jumps in a separate method
                }else{           
                    await RunExerciseAsync(exercise).ConfigureAwait(false); //the rest of the repetitions
                    }
                }
            if (repeatSet.TryGetValue(exercise.RepetitionID, out var repeatExercises) && !completedExerciseSets.Contains(exercise.RepetitionID))
            {
                completedExerciseSets.Add(exercise.RepetitionID);
                Console.WriteLine($"Repeating Exercises: {string.Join(", ", repeatExercises)}...");
                foreach (var repeatID in repeatExercises)
                {
                    var repeatExercise = exercises.FirstOrDefault(e => e.RepetitionID == repeatID);
                    if (repeatExercise != null)
                    {
                        await CheckPreparationCop(repeatExercise.PreparationCop,repeatExercise.LegsUsed);
                        SendExerciseConfiguration(repeatExercise);
                        await RunExerciseAsync(repeatExercise).ConfigureAwait(false);
                    }
                }
            }
        }
        Console.WriteLine("All exercises completed!");
        _isVisualizing = false;
    }

    private async Task CheckPreparationCop(int duration, string activeLeg) {
    Console.WriteLine($"[Preparation CoP]: Checking for {duration} sec (Active Leg: {activeLeg})...");
    DateTime startTime = DateTime.Now;
    while (true) 
    {
        bool isFootValid = false;
        (double Min, double Max) copRangeX = (-2.0, 2.0);
        (double Min, double Max) copRangeY = (-2.0, 2.0);

        double copXLeft = _copXLeft, copYLeft = _copYLeft;
        double copXRight = _copXRight, copYRight = _copYRight;

        if (activeLeg == "left")
        {
            isFootValid = copXLeft >= copRangeX.Min && copXLeft <= copRangeX.Max &&
                          copYLeft >= copRangeY.Min && copYLeft <= copRangeY.Max;
        }
        else if (activeLeg == "right")
        {
            isFootValid = copXRight >= copRangeX.Min && copXRight <= copRangeX.Max &&
                          copYRight >= copRangeY.Min && copYRight <= copRangeY.Max;
        }
        else if (activeLeg == "both") 
        {
            isFootValid = (copXLeft >= copRangeX.Min && copXLeft <= copRangeX.Max &&
                           copYLeft >= copRangeY.Min && copYLeft <= copRangeY.Max) &&
                          (copXRight >= copRangeX.Min && copXRight <= copRangeX.Max &&
                           copYRight >= copRangeY.Min && copYRight <= copRangeY.Max);
        }

        if (isFootValid)
        {
            if ((DateTime.Now - startTime).TotalSeconds >= duration)
            {
                Console.WriteLine($"[Preparation CoP]: {activeLeg} foot correctly positioned for the required time.");
                return;
            }
        }
        else
        {
            startTime = DateTime.Now; 
        }

        await Task.Delay(1000).ConfigureAwait(false); 
    }
}

private async Task Run5and6Async(ExerciseData exercise) // runs 4th and 5th exercises squats - walking lunges, lateral jumps
{
    Console.WriteLine($"[Exercise]: {exercise.Name} started for {exercise.TimingCop} seconds...");
    SendMessageToGUI($"[Exercise]: {exercise.Name} started for {exercise.TimingCop} seconds...");

    DateTime exerciseStartTime = DateTime.Now;
    int phase1Completed = 0; 
    int phaseRepeatCount = 0; 
    int previousZoneLeft = -1, previousZoneRight = -1;
    int feedbackLeft = -1, feedbackRight = -1;
    bool lostBalance = false;

    DateTime outOfZoneTimeLeft = DateTime.MinValue;
    DateTime outOfZoneTimeRight = DateTime.MinValue;

    while ((DateTime.Now - exerciseStartTime).TotalSeconds < exercise.TimingCop)
    {
        int phaseIndex = -1;
        if (phase1Completed == 0)
        {
            phaseIndex = 0; 
        }
        else if (phaseRepeatCount < 20)
        {
            phaseIndex = (phaseRepeatCount % 2 == 0) ? 1 : 2; // Move between phase 2 and 3
        }
        else if (exercise.ZoneSequence.Count == 4)
        {
            phaseIndex = 3; 
        }

        var phase = exercise.ZoneSequence[phaseIndex];
        Console.WriteLine($"[Phase {phaseIndex + 1}]: {phase.Duration} sec");

        DateTime phaseStartTime = DateTime.Now;
        lostBalance = false;
        int currentZoneLeft = -1, currentZoneRight = -1;
        if (phaseIndex == 3)
        {
            Console.WriteLine($"[Phase {phaseIndex + 1}]: No CoP check, waiting for {phase.Duration} seconds...");
            SendMessageToGUI($"[Phase {phaseIndex + 1}]: No CoP check, waiting...");
            await Task.Delay(phase.Duration * 1000);
            break;
        }
        while ((DateTime.Now - phaseStartTime).TotalSeconds < phase.Duration &&
               (DateTime.Now - exerciseStartTime).TotalSeconds < exercise.TimingCop)
        {
            double copXLeft = _copXLeft, copYLeft = _copYLeft;
            double copXRight = _copXRight, copYRight = _copYRight;

            if (phaseIndex == 1 || phaseIndex == 2)
            {
                var adjustedZonesLeft = AddCopLeft(exercise, phaseIndex);
                currentZoneLeft = Feedback(copXLeft, copYLeft,
                                           adjustedZonesLeft.greenZoneX, adjustedZonesLeft.greenZoneY,
                                           adjustedZonesLeft.redZoneX, adjustedZonesLeft.redZoneY);
            }
            else
            {
                currentZoneLeft = Feedback(copXLeft, copYLeft, phase.GreenZoneX, phase.GreenZoneY, phase.RedZoneX, phase.RedZoneY);
            }
            currentZoneRight = Feedback(copXRight, copYRight, phase.GreenZoneX, phase.GreenZoneY, phase.RedZoneX, phase.RedZoneY);

            if (currentZoneLeft != previousZoneLeft && currentZoneLeft > 0)
            {
                if (exercise.RepetitionID == 6 && phaseIndex == 2){
                currentZoneRight = -1;
            }
                Console.WriteLine($"[Exercise]: Left Foot Changed to Zone {currentZoneLeft}");
                SendMessageToGUI($"[Exercise]: Left Foot Changed to Zone {currentZoneLeft}");
                previousZoneLeft = currentZoneLeft;
                feedbackLeft = currentZoneLeft;
                SendFeedback(feedbackLeft, "Left");
            }
            if (currentZoneRight != previousZoneRight && currentZoneRight > 0){
            if (exercise.RepetitionID == 6 && phaseIndex == 1){
                currentZoneLeft = -1;
            }
                Console.WriteLine($"[Exercise]: Right Foot Changed to Zone {currentZoneRight}");
                SendMessageToGUI($"[Exercise]: Right Foot Changed to Zone {currentZoneRight}");
                previousZoneRight = currentZoneRight;
                feedbackRight = currentZoneRight;
                SendFeedback(feedbackRight, "Right");
            }
            if (currentZoneLeft == 1 || currentZoneRight == 1)
            {
                outOfZoneTimeLeft = DateTime.MinValue;
                outOfZoneTimeRight = DateTime.MinValue;
            }
            else
            {
                if (currentZoneLeft == 0 && outOfZoneTimeLeft == DateTime.MinValue)
                    outOfZoneTimeLeft = DateTime.Now;
                if (currentZoneRight == 0 && outOfZoneTimeRight == DateTime.MinValue)
                    outOfZoneTimeRight = DateTime.Now;

                bool leftFootOutTooLong = (outOfZoneTimeLeft != DateTime.MinValue) &&
                                          ((DateTime.Now - outOfZoneTimeLeft).TotalSeconds >= 4);
                bool rightFootOutTooLong = (outOfZoneTimeRight != DateTime.MinValue) &&
                                           ((DateTime.Now - outOfZoneTimeRight).TotalSeconds >= 4);

                if (leftFootOutTooLong || rightFootOutTooLong)
                {
                    lostBalance = true;
                    break;
                }
            }
            await Task.Delay(500).ConfigureAwait(false);;
        }
        if (lostBalance)
        {
            Console.WriteLine("You lost balance, restarting exercise...");
            SendMessageToGUI("You lost balance, restarting exercise...");

            if (previousZoneLeft != 7) SendFeedback(7, "Left");
            if (previousZoneRight != 7) SendFeedback(7, "Right");

            Console.WriteLine("Pausing for 5 seconds before restarting...");
            await Task.Delay(5000).ConfigureAwait(false);
            phase1Completed = 0;
            phaseRepeatCount = 0;
            previousZoneLeft = -1;
            previousZoneRight = -1;
            feedbackLeft = -1;
            feedbackRight = -1;
            outOfZoneTimeLeft = DateTime.MinValue;
            outOfZoneTimeRight = DateTime.MinValue;
            lostBalance = false; // Reset flag
            exerciseStartTime = DateTime.Now;
            Console.WriteLine("Restarting exercise...");
            continue;
        }
        if (phaseIndex == 0)
        {
            phase1Completed = 1;
        }
        else if (phaseIndex == 1 || phaseIndex == 2)
        {
            phaseRepeatCount++;
        }
    }

    Console.WriteLine("[Client]: Put leg down");
    SendMessageToGUI("[Client]: Put leg down");
    await Task.Delay(exercise.Release * 1000);
}

        private async Task RunExerciseAsync(ExerciseData exercise)
{
        DateTime outOfZoneTimeLeft = DateTime.MinValue;
        DateTime outOfZoneTimeRight = DateTime.MinValue;
    if (exercise.RepetitionID == 1 || exercise.RepetitionID == 2)
    {
        await Task.Delay(3000).ConfigureAwait(false);  // shows exercise text for 3 seconds so both client and HMD wait
    }
    Console.WriteLine($"[Exercise]: {exercise.Name} started for {exercise.TimingCop} seconds...");
    SendMessageToGUI($"[Exercise]: {exercise.Name} started for {exercise.TimingCop} seconds...");
        DateTime exerciseStartTime = DateTime.Now;
        int phaseIndex = 0;
        int previousZoneLeft = -1, previousZoneRight = -1;
        int feedbackLeft = -1, feedbackRight = -1;

        while ((DateTime.Now - exerciseStartTime).TotalSeconds < exercise.TimingCop)
        {
            var phase = exercise.ZoneSequence[phaseIndex];
            Console.WriteLine($"[Phase {phaseIndex + 1}]: {phase.Duration} sec");
            DateTime phaseStartTime = DateTime.Now;
            bool lostBalance = false;
            int currentZoneLeft = 0, currentZoneRight = 0;
            //noCOP check for phase in exercise/ moves to the next phase afterwards
            if (exercise.RepetitionID == 4 && phaseIndex == 2)   // Exercise 5, Phase 4 (Index 3)
        {
            Console.WriteLine($"[Phase {phaseIndex + 1}]: No CoP check, waiting for {phase.Duration} seconds...");
            SendMessageToGUI($"[Phase {phaseIndex + 1}]: No CoP check, waiting...");

            await Task.Delay(phase.Duration * 1000);
            phaseIndex++; // Move to the next phase
            continue;
        }

            while ((DateTime.Now - phaseStartTime).TotalSeconds < phase.Duration &&
                   (DateTime.Now - exerciseStartTime).TotalSeconds < exercise.TimingCop)
            {
                double copXLeft = _copXLeft, copYLeft = _copYLeft;
                double copXRight = _copXRight, copYRight = _copYRight;

                if (exercise.LegsUsed == "right")
                {
                    currentZoneRight = Feedback(copXRight, copYRight, phase.GreenZoneX, phase.GreenZoneY, phase.RedZoneX, phase.RedZoneY);
                    currentZoneLeft = -1;
                }
                else if (exercise.LegsUsed == "left")
                {
                    currentZoneLeft = Feedback(copXLeft, copYLeft, phase.GreenZoneX, phase.GreenZoneY, phase.RedZoneX, phase.RedZoneY);
                    currentZoneRight = -1;
                }
                else if (exercise.LegsUsed == "both")
                {
                    currentZoneLeft = Feedback(copXLeft, copYLeft, phase.GreenZoneX, phase.GreenZoneY, phase.RedZoneX, phase.RedZoneY);
                    currentZoneRight = Feedback(copXRight, copYRight, phase.GreenZoneX, phase.GreenZoneY, phase.RedZoneX, phase.RedZoneY);
                }

                if (currentZoneLeft != previousZoneLeft && currentZoneLeft > 0)
                {
                    Console.WriteLine($"[Exercise]: Left Foot Changed to Zone {currentZoneLeft}");
                    SendMessageToGUI($"[Exercise]: Left Foot Changed to Zone {currentZoneLeft}");
                    previousZoneLeft = currentZoneLeft;
                    feedbackLeft = currentZoneLeft;
                    SendFeedback(feedbackLeft, "Left");
                }
                if (currentZoneRight != previousZoneRight && currentZoneRight > 0)
                {
                    Console.WriteLine($"[Exercise]: Right Foot Changed to Zone {currentZoneRight}");
                    SendMessageToGUI($"[Exercise]: Right Foot Changed to Zone {currentZoneRight}");
                    previousZoneRight = currentZoneRight;
                    feedbackRight = currentZoneRight;
                    SendFeedback(feedbackRight, "Right");
                }
                if (currentZoneLeft == 1)
                {
                    outOfZoneTimeLeft = DateTime.MinValue;
                }
                else if (currentZoneRight == 1)
                {
                outOfZoneTimeRight = DateTime.MinValue;
                }
                else
                {
                    if (currentZoneLeft == 0 && outOfZoneTimeLeft == DateTime.MinValue)
                    {
                        outOfZoneTimeLeft = DateTime.Now;
                    }
                    if (currentZoneRight == 0 && outOfZoneTimeRight == DateTime.MinValue)
                    {
                        outOfZoneTimeRight = DateTime.Now;
                    }

                    bool leftFootOutTooLong = (outOfZoneTimeLeft != DateTime.MinValue) &&
                                              ((DateTime.Now - outOfZoneTimeLeft).TotalSeconds >= 4);
                    bool rightFootOutTooLong = (outOfZoneTimeRight != DateTime.MinValue) &&
                                               ((DateTime.Now - outOfZoneTimeRight).TotalSeconds >= 4);
                    if (leftFootOutTooLong || rightFootOutTooLong)
                    {
                        lostBalance = true;
                        break;
                    }
                }
            }
            if (lostBalance)
            {
                Console.WriteLine("You lost balance, restarting exercise...");
                SendMessageToGUI("You lost balance, restarting exercise...");

                if (exercise.LegsUsed == "both" || exercise.LegsUsed == "left")
                {
                    if (previousZoneLeft != 7)
                    {
                        SendFeedback(7, "Left");
                        previousZoneLeft = 7;
                    }
                }
                if (exercise.LegsUsed == "both" || exercise.LegsUsed == "right")
                {
                    if (previousZoneRight != 7)
                    {
                        SendFeedback(7, "Right");
                        previousZoneRight = 7;
                    }
                }
                Console.WriteLine("Pausing for 5 seconds before restarting...");
                await Task.Delay(5000).ConfigureAwait(false);
                phaseIndex = 0;
                previousZoneLeft = -1;
                previousZoneRight = -1;
                feedbackLeft = -1;
                feedbackRight = -1;
                outOfZoneTimeLeft = DateTime.MinValue;
                outOfZoneTimeRight = DateTime.MinValue;
                lostBalance = false; // Reset flag
                exerciseStartTime = DateTime.Now;
                Console.WriteLine("Restarting exercise...");
                continue;
            }
            phaseIndex++;
            if (phaseIndex >= exercise.ZoneSequence.Count)
            {
                phaseIndex = 0; // Loop through multiple phases within 30 seconds
            }
        }
        Console.WriteLine("[Client]: Put leg down");
        SendMessageToGUI("[Client]: Put leg down");
        await Task.Delay(exercise.Release * 1000);
}


        private int Feedback(double copX, double copY,  
                          (double Min, double Max) greenZoneX, (double Min, double Max) greenZoneY, 
                          (double Min, double Max) redZoneX, (double Min, double Max) redZoneY)
{
    bool isInGreenZone = copX >= greenZoneX.Item1 && copX <= greenZoneX.Item2 &&
                                 copY >= greenZoneY.Item1 && copY <= greenZoneY.Item2;
    if (isInGreenZone)
                return 1; //Green Zone   
    bool isInRedZone = copX >= redZoneX.Item1 && copX <= redZoneX.Item2 &&
                                 copY >=  redZoneY.Item1 && copY <=  redZoneY.Item2; 
    if (isInRedZone){     
    if (copX > 0 && copY > 0)
                return 3; //Front Right
            else if (copX < 0 && copY > 0)
                return 4; //Front Left
            else if (copX > 0 && copY < 0)
                return 5; //Back Right
            else if (copX < 0 && copY < 0)
                return 6; //Back Left
    }
    return 0 ;
}

                private (int duration, (double, double) greenZoneX, (double, double) greenZoneY, (double, double) redZoneX, (double, double) redZoneY) AddCopLeft(ExerciseData exercise, int phaseIndex)
                {
                (int, (double, double), (double, double), (double, double), (double, double)) phaseData;
                if (exercise.RepetitionID == 5 && phaseIndex == 2)
                {
                    phaseData = (2, (-1.5, 1.5), (0.3, 5.5), (-2.0, 2.0), (0.0, 4.0));
                }
                else if (exercise.RepetitionID == 5 && phaseIndex == 3)
                {
                    phaseData = (2, (-1.5, 1.5), (-3.0, 3.0), (-1.9, 1.9), (-4.0, 4.0));
                }
                else if (exercise.RepetitionID == 6 && phaseIndex == 2)
                {
                    phaseData = (2, (-1.0, 1.0), (0.2, 1.9), (-1.5, 1.5), (0.0, 4.0));
                }
                else if (exercise.RepetitionID == 6 && phaseIndex == 3)
                {
                    phaseData = (2, (-1.0, 1.0), (0.2, 1.9), (-1.5, 1.5), (0.0, 4.0));
                }
                else
                {
                    phaseData = (0, (0, 0), (0, 0), (0, 0), (0, 0));
                }
                return phaseData;
                }
        private void SendFeedback(int feedbackCode, string foot)
        {
            Console.WriteLine($"[Feedback]: Received feedback code for {foot} foot: {feedbackCode}");
            var feedbackMessage = new FeedbackMessage
            {
                MessageType = "Feedback",
                RepetitionID = currentExerciseID,
                Foot = foot,
                Zone = feedbackCode
            };
            SendDataToHMD(feedbackMessage);
        }

        private void SendExerciseConfiguration(ExerciseData exercise)
        {
            Console.WriteLine($"[Feedback]: Sending exercise configuration for exercise {exercise.RepetitionID}");
            var configMessage = new ExerciseConfigMessage
            {
                MessageType = "ExerciseConfig",
                RepetitionID = exercise.RepetitionID,
                Name = exercise.Name,
                LegsUsed = exercise.LegsUsed,
                Intro = exercise.Intro,
                Demo = exercise.Demo,
                PreparationCop = exercise.PreparationCop,
                TimingCop = exercise.TimingCop,
                Release = exercise.Release,
                Sets = exercise.Sets,
                ZoneSequence = exercise.ZoneSequence
            };
            SendDataToHMD(configMessage); 
        }

        private void HandleStopCommand()
        {
            if (!_isVisualizing)
            {
                Console.WriteLine("[Client]: Visualization is not running.");
                return;
            }
            Console.WriteLine("[Client]: Stopping visualization and data streams...");
            _server.StopDataStream();
            CloseVisualizationWindow();
            _isVisualizing = false;
            CloseHMD();
            Console.WriteLine("[Client]: Visualization and data streams stopped.");
        }

        private void SubscribeToDeviceUpdates()
        {
            var activeDevices = _server.Manager.GetConnectedDevices();
            if (!activeDevices.Any())
            {
                Console.WriteLine("[Client]: No active devices to subscribe to.");
                return;
            }
            foreach (var device in activeDevices)
            {
                device.CoPUpdated -= OnCoPUpdated;
                device.CoPUpdated += OnCoPUpdated;
            }
        }

       private void OnCoPUpdated(object sender, (string DeviceName, double CoPX, double CoPY, double[] Pressures) copData)
{
    if (_visualizationWindow == null || _visualizationWindow.IsDisposed) return;

    if (sender is Device device)
    {
         if ((_currentExercise?.RepetitionID == 5 || _currentExercise?.RepetitionID == 6) && _currentPhase == 4)
        {
            Console.WriteLine($"[Client]: Skipping CoP check for Exercise {_currentExercise.RepetitionID}, Phase 4.");
            return; 
        }
        if (device.IsLeftSock)
        {
            _copXLeft = copData.CoPX;
            _copYLeft = copData.CoPY;
        }
        else
        {
            _copXRight = copData.CoPX;
            _copYRight = copData.CoPY;
        }

        _visualizationWindow.UpdateVisualization(
            xLeft: _copXLeft,
            yLeft: _copYLeft,
            pressuresLeft: device.IsLeftSock ? copData.Pressures : Array.Empty<double>(),
            xRight: _copXRight,
            yRight: _copYRight,
            pressuresRight: !device.IsLeftSock ? copData.Pressures : Array.Empty<double>()
        );
    }
}

        private void OpenVisualizationWindow()
        {
            if (_visualizationWindow == null || _visualizationWindow.IsDisposed)
            {
                Thread visualizationThread = new Thread(() =>
                {
                    _visualizationWindow = new VisualizationWindow();
                    System.Windows.Forms.Application.Run(_visualizationWindow);

                });
                visualizationThread.SetApartmentState(ApartmentState.STA);
                visualizationThread.IsBackground = true;
                visualizationThread.Start();
            }
        }

        private void CloseVisualizationWindow()
        {
            if (_visualizationWindow != null && !_visualizationWindow.IsDisposed)
            {
                _visualizationWindow.Invoke(new Action(() => _visualizationWindow.Close()));
                _visualizationWindow = null;
            }
        }

        //################################### HMD ########################################
        private void HandleHMDCommand()
        {
            if (_bypassHMD)
            {
                Console.WriteLine("HMD is bypassed. Commands disabled.");
                return;
            }
            Console.WriteLine("1. Connect to HMD\n2. Disconnect from HMD\n3. Exit HMD Menu");
            Console.Write("> ");
            string input = Console.ReadLine()?.Trim();
            if (input == "1")
                ConnectToHMD(questip, 9001);
            else if (input == "2")
                DisconnectFromHMD();
        }

       private void ConnectToHMD(string ipAddress, int port)
{
    if (_bypassHMD)
            {
                Console.WriteLine("HMD bypassed. Not connecting.");
                return;
            }
    try
    {
        // Ensure HMD application is running
        string processName = Path.GetFileNameWithoutExtension(hmdpath);
        Process[] hmdProcesses = Process.GetProcessesByName(processName);
        if (hmdProcesses.Length == 0)
        {
            Console.WriteLine("Starting HMD application...");
            try
            {
                // _hmdProcess = Process.Start(hmdpath);
                // Thread.Sleep(5000); // ATM UNNECESSARY
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start HMD application: {ex.Message}");
                return;
            }
        }

        // Check if already connected (more robust check)
        if (_hmdClient != null && _hmdClient.Connected)
        {
            try
            {
                if (_hmdClient.Client.Poll(0, SelectMode.SelectRead) && _hmdClient.Client.Available == 0)
                {
                    Console.WriteLine("Connection lost. Reconnecting...");
                }
                else
                {
                    Console.WriteLine("Already connected to HMD.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection check failed: {ex.Message}");
            }
        }

        // Attempt to connect with retries
        int retries = 10;
        int delay = 3000;
        for (int i = 0; i < retries; i++)
        {
            try
            {
                // Close previous failed client before retrying
                _hmdClient?.Close();
                _hmdClient = new TcpClient(ipAddress, port);
                _hmdStream = _hmdClient.GetStream();
                
                Console.WriteLine("HMD Connected.");
                return; // Connection successful
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection attempt {i + 1} failed: {ex.Message}. Retrying...");
                Thread.Sleep(delay);
            }
        }

        Console.WriteLine("Failed to connect to HMD after multiple attempts.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error connecting to HMD: {ex.Message}");
    }
}
        private void DisconnectFromHMD()
        {
            if (_bypassHMD)
            {
                Console.WriteLine("HMD bypassed. Not disconnecting.");
                return;
            }
            _hmdStream?.Close();
            _hmdClient?.Close();
            _hmdStream = null;
            _hmdClient = null;
            Console.WriteLine("HMD Disconnected.");
        }

        private void CloseHMD()
{
    if (_bypassHMD)
            {
                Console.WriteLine("HMD bypassed. Not closing.");
                return;
            }
    try
    {
        if (_hmdProcess != null && !_hmdProcess.HasExited)
        {
            _hmdStream?.Close();
            _hmdClient?.Close();
            _hmdStream = null;
            _hmdClient = null;
            Console.WriteLine("Closing HMD application...");
            _hmdProcess.Kill();  // Kill the process
            _hmdProcess.WaitForExit();  // Ensure the process has exited before continuing
            Console.WriteLine("HMD application closed.");
        }
        else
        {
            Console.WriteLine("HMD application is not running.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to close HMD application: {ex.Message}");
    }
}

        private void SendDataToHMD(object data)
        {
            if (_bypassHMD)
            {
                Console.WriteLine("HMD bypassed. Data not sent.");
                return;
            }
            try
            {
                if(data != null){
                      string jsonData = JsonSerializer.Serialize(data);
                byte[] dataBytes = Encoding.UTF8.GetBytes(jsonData);
                _hmdStream.Write(dataBytes, 0, dataBytes.Length);
                _hmdStream.Flush();  
                }else{
                    Console.WriteLine("no work");
    
                }
                
            }
            catch (Exception ex) { Console.WriteLine($"Error sending data: {ex.Message}"); }
        }

        //################################### GUI communication ########################################
        private void OpenGUI(int port)
{
    try
    {
        // Pass the port as a command line argument to the GUI process.
        Process.Start(guipath, port.ToString());
        Console.WriteLine($"GUI launched on port {port}. Waiting for connection...");
        Thread.Sleep(2000);
        _guiClient = new TcpClient("127.0.0.1", port);
        _guiStream = _guiClient.GetStream();
        Console.WriteLine("Connected to GUI.");
        Task.Run(() => ListenForGUIResponses());
        SendMessageToGUI("connect");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to connect to GUI: {ex.Message}");
    }
}

        private async Task ListenForGUIResponses()
{
    byte[] buffer = new byte[1024];
    while (_guiClient?.Connected == true)
    {
        try
        {
            int bytesRead = await _guiStream.ReadAsync(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                Console.WriteLine("[Client]: Connection closed by GUI.");
                break;
            }
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
            if (response.StartsWith("config:", StringComparison.OrdinalIgnoreCase))
            {
                int userId = int.Parse(response.Substring("config:".Length).Split(',')[0]);
                Console.WriteLine($"[Client]: Received user ID: {userId}");
                CreateLogFiles(userId);

            }
            else if (response.ToLower() == "connect")
            {
                Console.WriteLine("[Client]: Connect command received from GUI.");
                await HandleConnectCommandAsync(CancellationToken.None);
            }
            else if (response.ToLower() == "calibrate")
            {
                Console.WriteLine("[Client]: Calibrate command received from GUI.");
                HandleCalibrateCommand();
            }
            else if (response.ToLower() == "start")
            {
                Console.WriteLine("[Client]: Start command received from GUI.");
                await HandleStartCommand();
            }
            else if (response.ToLower() == "stop")
            {
                Console.WriteLine("[Client]: Stop command received from GUI.");
                HandleStopCommand();
            }
            else if (response.ToLower() == "hmd")
            {
                Console.WriteLine("[Client]: HMD command received from GUI.");
                HandleHMDCommand();
            }
            else if (response.ToLower() == "1")
            {
                Console.WriteLine("[Client]: 1 command received from GUI.");
                ConnectToHMD(questip, 9001);
            }
            else if (response.ToLower() == "2")
            {
                Console.WriteLine("[Client]: 2 command received from GUI.");
                DisconnectFromHMD();
            }
            else if (response.ToLower() == "exit")
            {
                Console.WriteLine("[Client]: Exit command received from GUI.");
                HandleExitCommand();
                Environment.Exit(0);
            }
            else
            {
                Console.WriteLine("[Client]: Unrecognized message from GUI.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while listening for GUI responses: {ex.Message}");
            break;
        }
    }
    Console.WriteLine("[Client]: Stopped listening for GUI responses.");
}
       private void CreateLogFiles(int userId)
{
    try
    {
        CreateLogFile(userId, "Left");
        CreateLogFile(userId, "Right");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error creating log files: {ex.Message}");
    }
}

private void CreateLogFile(int userId, string sockType)
{
    int fileIndex = 1;
    string baseFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"UserID_{userId}_{sockType}_{fileIndex}.txt");
    string logFilePath = baseFilePath;
    while (File.Exists(logFilePath))
    {
        fileIndex++;
        logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"UserID_{userId}_{sockType}_{fileIndex}.txt");
    }
    using (FileStream fs = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
    using (StreamWriter writer = new StreamWriter(fs))
    {
        writer.WriteLine("Timestamp\tBattery\tTime_ms\tQ0\tQ1\tQ2\tQ3\tAcc_X\tAcc_Y\tAcc_Z\tSensor1\tSensor2\tSensor3\tSensor4\tSensor5\tSensor6\tSensor7\tSensor8");
    }
    string activeLogFile = $"ActiveLogFile_{sockType}.txt";
    using (FileStream fs = new FileStream(activeLogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
    using (StreamWriter writer = new StreamWriter(fs))
    {
        writer.WriteLine(logFilePath);
    }
    Console.WriteLine($"Log file created for {sockType} sock: {logFilePath}");
}



        private void SendMessageToGUI(string message)
        {
            try
            {
                if (_guiClient == null || !_guiClient.Connected)
                {
                    Console.WriteLine("Not connected to GUI.");
                    return;
                }
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                _guiStream.Write(messageBytes, 0, messageBytes.Length);
                _guiStream.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending message to GUI: {ex.Message}");
            }
        }

        private void CloseGUI()
        {
            try
            {
                if (_guiClient?.Connected == true)
                {
                    _guiStream?.Close();
                    _guiClient?.Close();
                    Console.WriteLine("[Client]: Disconnected from GUI.");
                }

                Process[] processes = Process.GetProcessesByName("clientgui");
                foreach (var process in processes)
                {
                    process.Kill();
                    Console.WriteLine("[Client]: GUI process terminated.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client]: Error closing GUI: {ex.Message}");
            }
        }
    }

    // Message classes for JSON serialization
    public class FeedbackMessage
    {
        public string MessageType { get; set; }
        public int RepetitionID { get; set; }
        public string Foot { get; set; }
        public int Zone { get; set; }
    }
    

    public class ExerciseConfigMessage
    {
        public string MessageType { get; set; }
        public int RepetitionID { get; set; }
        public string Name { get; set; }
        public string LegsUsed { get; set; }
        public int Intro { get; set; }
        public int Demo { get; set; }
        public int PreparationCop { get; set; }
        public int TimingCop { get; set; }
        public int Release { get; set; }
        public int Switch { get; set; }
        public int Sets { get; set; }
        public System.Collections.Generic.List<ZoneSequenceItem> ZoneSequence { get; set; }
    }
}
