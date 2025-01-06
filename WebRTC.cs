using Microsoft.MixedReality.WebRTC;
using NAudio.Wave;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;
using static Microsoft.MixedReality.WebRTC.DataChannel;

namespace WinFormsRTC
{
    public class WebRTC
    {
        private PeerConnection pc;
        private DataChannel dataChannel;
        private DeviceAudioTrackSource _microphoneSource;
        private LocalAudioTrack _localAudioTrack;
        private Transceiver _audioTransceiver;
        private WaveInEvent waveInEvent;
        private WaveOutEvent waveOutEvent;
        private BufferedWaveProvider waveProvider;
        private static readonly HttpClient client = new HttpClient();
        private string openaiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""; // Replace with your OpenAI API key
        private static readonly string OpenaiApiUrl = "https://api.openai.com/v1/realtime";
        private static readonly string DefaultInstructions = "You are helpful and have some tools installed.\n\nIn the tools you have the ability to control a robot hand.";

        public WebRTC()
        {
            waveInEvent = new WaveInEvent();
            waveInEvent.DataAvailable += WaveInEvent_DataAvailable; // Bind callback

            waveProvider = new BufferedWaveProvider(new WaveFormat(16000, 16, 1));
            waveProvider.BufferLength = 1024 * 16;
            waveProvider.DiscardOnBufferOverflow = true;

            waveOutEvent = new WaveOutEvent();
            waveOutEvent.Volume = 0.5f;
            waveOutEvent.Init(waveProvider);
        }

        public void StartCall()
        {
            waveInEvent.StartRecording();

            // Start establishing the call and exchange SDP and ICE information
            Task.Run(() => InitializeConnectionAsync());
        }

        public async Task InitializeConnectionAsync()
        {
            var tokenResponse = await GetSessionAsync();
            var data = JsonConvert.DeserializeObject<dynamic>(tokenResponse);
            string ephemeralKey = data.client_secret.value;

            // Create PeerConnection instance
            pc = new PeerConnection();

            var config = new PeerConnectionConfiguration
            {
                IceServers = new List<IceServer> {
                            new IceServer{ Urls = { "stun:stun.l.google.com:19302" } }
                        }
            };

            await pc.InitializeAsync(config);
            //await pc.InitializeAsync();

            pc.IceStateChanged += Pc_IceStateChanged;

            // Create data channel
            dataChannel = await pc.AddDataChannelAsync(1, "response", true, true);

            // Set message received callback
            dataChannel.MessageReceived += DataChannel_MessageReceived;
            dataChannel.StateChanged += DataChannel_StateChanged;

            _microphoneSource = await DeviceAudioTrackSource.CreateAsync();
            _localAudioTrack = LocalAudioTrack.CreateFromSource(_microphoneSource, new LocalAudioTrackInitConfig { trackName = "microphone_track" });

            _audioTransceiver = pc.AddTransceiver(MediaKind.Audio);
            _audioTransceiver.DesiredDirection = Transceiver.Direction.SendReceive;
            _audioTransceiver.LocalAudioTrack = _localAudioTrack;

            pc.LocalSdpReadytoSend += async (sdp) =>
            {
                Console.WriteLine("Local SDP offer (copy to Peer 2):");
                Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(sdp.Content)));

                string modifiedSdp = SetPreferredCodec(sdp.Content, "opus/48000/2");

                // Send local SDP to OpenAI
                string openAiSdpStr = await ConnectRTCAsync(ephemeralKey, new SdpMessage { Content = modifiedSdp, Type = SdpMessageType.Offer });
                //string openAiSdpStr = await ConnectRTCAsync(ephemeralKey, new SdpMessage { Content = sdp.Content, Type = SdpMessageType.Offer });
                Console.WriteLine($"OpenAI SDP Response: {openAiSdpStr}");

                SdpMessage openAiSdpObj = new SdpMessage()
                {
                    Content = openAiSdpStr,
                    Type = SdpMessageType.Answer
                };

                // Set the remote SDP returned by OpenAI to the PC
                await pc.SetRemoteDescriptionAsync(openAiSdpObj);
            };

            pc.AudioTrackAdded += Pc_AudioTrackAdded;

            bool offer = pc.CreateOffer();
        }

        private void Pc_IceStateChanged(IceConnectionState newState)
        {
            Console.WriteLine($"ICE State Changed: {newState}");
            if (newState == IceConnectionState.Connected)
            {
                Console.WriteLine("ICE Connected, dataChannel should be open soon.");
            }
            else if (newState == IceConnectionState.Failed)
            {
                Console.WriteLine("ICE Connection Failed. Please check network configurations.");
            }
        }

        private void WaveInEvent_DataAvailable(object? sender, WaveInEventArgs e)
        {
            // Send audio data as byte[]
            byte[] audioData = e.Buffer;

            if (dataChannel?.State == ChannelState.Open)
            {
                // Send recorded audio data to the remote
                dataChannel.SendMessage(audioData);
            }
        }

        private void Pc_AudioTrackAdded(RemoteAudioTrack track)
        {
            track.AudioFrameReady += Track_AudioFrameReady;
        }

        private void Track_AudioFrameReady(AudioFrame frame)
        {
            if (frame.audioData == IntPtr.Zero || frame.sampleCount == 0)
            {
                Console.WriteLine("Audio frame is invalid.");
                return;
            }

            // Convert audio data from IntPtr to byte array
            byte[] audioData = new byte[frame.sampleCount * (frame.bitsPerSample / 8) * (int)frame.channelCount];
            Marshal.Copy(frame.audioData, audioData, 0, audioData.Length);

            // If audio is 16-bit, convert to short[]
            if (frame.bitsPerSample == 16)
            {
                short[] shortAudioData = new short[audioData.Length / 2];
                Buffer.BlockCopy(audioData, 0, shortAudioData, 0, audioData.Length);

                // Add audio data to BufferedWaveProvider
                byte[] pcmData = new byte[shortAudioData.Length * 2];
                Buffer.BlockCopy(shortAudioData, 0, pcmData, 0, pcmData.Length);
                waveProvider.AddSamples(pcmData, 0, pcmData.Length);
            }
            else
            {
                // If audio is 8-bit, directly add audio data
                waveProvider.AddSamples(audioData, 0, audioData.Length);
            }

            // Play audio data
            if (waveOutEvent.PlaybackState != PlaybackState.Stopped)
            {
                waveOutEvent.Play();
            }
        }

        private void DataChannel_StateChanged()
        {
            if (dataChannel?.State == ChannelState.Open)
            {
                //StartSendingData();
            }
        }

        private void DataChannel_MessageReceived(byte[] message)
        {
            string decodedMessage = Encoding.UTF8.GetString(message);
            Console.WriteLine("Received message: " + decodedMessage);
        }

        private string SetPreferredCodec(string sdp, string preferredCodec)
        {
            var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

            // Find m=audio line
            var audioLineIndex = lines.FindIndex(line => line.StartsWith("m=audio"));
            if (audioLineIndex == -1)
            {
                Console.WriteLine("No audio line found in SDP.");
                return sdp;
            }

            // Get m=audio line and split it into space-separated parts
            var audioLineParts = lines[audioLineIndex].Split(' ').ToList();

            // Find all payload type IDs from m=audio line
            var codecPayloadTypes = audioLineParts.Skip(3).ToList(); // Payload types start from the fourth element

            // Find the rtpmap line corresponding to the payload type
            var codecMap = new Dictionary<string, string>(); // payload type -> codec name
            foreach (var line in lines)
            {
                if (line.StartsWith("a=rtpmap"))
                {
                    var parts = line.Split(new[] { ':', ' ' }, StringSplitOptions.None);
                    if (parts.Length >= 3)
                    {
                        codecMap[parts[1]] = parts[2]; // payload type -> codec name
                    }
                }
            }

            // Find the preferred codec payload type
            var preferredPayloadType = codecMap.FirstOrDefault(x => x.Value.StartsWith(preferredCodec)).Key;
            if (preferredPayloadType == null)
            {
                Console.WriteLine($"Preferred codec '{preferredCodec}' not found in SDP.");
                return sdp;
            }

            // Move the preferred payload type to the beginning of the m=audio line
            audioLineParts.Remove(preferredPayloadType);
            audioLineParts.Insert(3, preferredPayloadType); // Insert at the fourth position (after "m=audio", port, protocol)

            // Replace the m=audio line
            lines[audioLineIndex] = string.Join(" ", audioLineParts);

            return string.Join("\r\n", lines);
        }

        public async Task<string> GetSessionAsync()
        {
            try
            {
                // Construct request data
                var requestBody = new
                {
                    model = "gpt-4o-realtime-preview-2024-12-17",
                    voice = "verse",
                    modalities = new[] { "audio", "text" },
                    instructions = "You are a friendly assistant."
                };

                // Create request body
                var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

                // Create request
                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/realtime/sessions")
                {
                    Headers =
                    {
                        { "Authorization", $"Bearer {openaiApiKey}" }
                    },
                    Content = content
                };

                // Send request and get response
                var response = await client.SendAsync(request);

                // Check response status
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Error: {response.StatusCode}, {response.ReasonPhrase}");
                }

                // Read response content and return
                var responseContent = await response.Content.ReadAsStringAsync();
                return responseContent;
            }
            catch (Exception ex)
            {
                // Error handling
                Console.WriteLine($"An error occurred: {ex.Message}");
                return null;
            }
        }

        public async Task<string> ConnectRTCAsync(string ephemeralKey, SdpMessage localSdp)
        {
            // Build URL and add query parameters
            var url = $"{OpenaiApiUrl}?model=gpt-4o-realtime-preview-2024-12-17&instructions={Uri.EscapeDataString(DefaultInstructions)}&voice=ash";

            // Set request headers
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ephemeralKey);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sdp"));

            // Set request body
            var content = new StringContent(localSdp.Content);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/sdp");

            // Send POST request to OpenAI API
            var response = await client.PostAsync(url, content);

            // If the response status code is not 2xx, throw an exception
            if (!response.IsSuccessStatusCode)
            {
                var errorResponse = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Error: {response.StatusCode} - {errorResponse}");
                throw new HttpRequestException($"OpenAI API error: {response.StatusCode}");
            }

            // Get and return response content (string)
            return await response.Content.ReadAsStringAsync();
        }
    }
}
