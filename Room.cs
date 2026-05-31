using System.Diagnostics;
using Newtonsoft.Json;

namespace MainServer
{
    public class Room
    {
        public int port;
        public int id;
        public RoomStatus status;
        public List<int> playerList = [];

        private readonly string serverPath = "C:/Users/Yoruko/Desktop/CS/Server/CS_Server.exe";

        public Room(int port, int id)
        {
            this.port = port;
            this.id = id;
        }

        public void Join(Player player)
        {
            playerList.Add(player.uid);
        }

        public void Remove(Player player)
        {
            playerList.Remove(player.uid);
        }

        public void StartRoom()
        {
            if (!File.Exists(serverPath))
            {
                Console.WriteLine($"Could not find server executable file: {serverPath}");
                return;
            }
            Console.WriteLine($"Room {port} start playing.");
            status = RoomStatus.Playing;

            string jsonPlayerList = JsonConvert.SerializeObject(playerList);

            ProcessStartInfo info = new()
            {
                FileName = serverPath,
                Arguments = $"-p {port} -l \"{jsonPlayerList}\"",
                CreateNoWindow = false,
                UseShellExecute = false
            };
            Process process = new() { StartInfo = info };
            process.Start();

        }
    }
}
