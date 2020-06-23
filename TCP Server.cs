using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TCPServer
{
	class Server
	{
		/// <summary>
		/// メッセージ表示判定
		/// </summary>
		private readonly bool isMessage;

		/// <summary>
		/// ログ表示判定
		/// </summary>
		private readonly bool isLog;

		/// <summary>
		/// ファイルに書き込むログの内容
		/// </summary>
		private readonly BlockingCollection<string> msg = new BlockingCollection<string>();

		/// <summary>
		/// ServerのIPアドレス
		/// </summary>
		private IPAddress IP;

		/// <summary>
		/// TCPポートがオープンしているか否かの判定
		/// </summary>
		private bool isOpen = false;

		/// <summary>
		/// TCPポートがオープンしているか否かの判定
		/// </summary>
		internal bool IsOpen
		{
			get { return isOpen; }
		}

		/// <summary>
		/// TCPサーバのソケット
		/// </summary>
		private Socket Sock;

		/// <summary>
		/// 受信バッファサイズ
		/// </summary>
		private int bufferSize;

		/// <summary>
		/// 接続待機のイベント
		/// </summary>
		private readonly ManualResetEvent AllDone = new ManualResetEvent(false);

		/// <summary>
		/// クライアント一覧
		/// NuGetでSystem.ServiceModel.Primitivesをインストール
		/// </summary>
		private readonly SynchronizedCollection<Socket> ClientSockets = new SynchronizedCollection<Socket>();

		/// <summary>
		/// 受信時に実行する外部メソッド
		/// 必要に応じて型を変える(型を増やす)
		/// </summary>
		private readonly Func<string, string> Method;

		/// <summary>
		/// コンストラクタ
		/// </summary>
		/// <param name="ismessage">true=エラー発生時にメッセージを表示する</param>
		/// <param name="islog">true=エラー発生時にログを記録する</param>
		/// <param name="method">データ受信時に実行するメソッド</param>
		internal Server(bool ismessage, bool islog, Func<string, string> method)
		{
			isMessage = ismessage;
			isLog = islog;
			Method = method;
			// Encoding.GetEncoding("shift_jis")で例外回避に必要
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			// エラーログを記録するならログ書き込みタスクを動作させる
			if (isLog == true)
			{
				logWrite(AppDomain.CurrentDomain.BaseDirectory + "TCP Server Error.csv");
			}
		}

		/// <summary>
		/// デストラクタで念のため閉じる処理
		/// </summary>
		~Server()
		{
			Close();
			msg.Dispose();
		}

		/// <summary>
		/// ログの追加
		/// </summary>
		/// <param name="message">書き込むメッセージ</param>
		private void message(string message)
		{
			_ = msg.TryAdd(DateTime.Now.ToString("yyyy/M/d HH:mm:ss,") + message, Timeout.Infinite);
		}

		/// <summary>
		///  エラーログを記録
		/// </summary>
		/// <param name="message">エラー内容</param>
		private void logWrite(string fileName)
		{
			// 最初にファイルの有無を確認
			if (File.Exists(fileName) == false)
			{
				using FileStream hStream = File.Create(fileName);
				// 作成時に返される FileStream を利用して閉じる
				hStream?.Close();
			}
			_ = Task.Run(async () =>
			{
				while (true)
				{
					try
					{
						_ = msg.TryTake(out string log, Timeout.Infinite);
						while (true)
						{
							try
							{
								// ファイルがロックされている場合例外が発生して以下の処理は行わずリトライとなる
								using FileStream stream = new FileStream(fileName, FileMode.Open);
								// ログ書き込み
								using FileStream fs = new FileStream(fileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
								using StreamWriter sw = new StreamWriter(fs, Encoding.GetEncoding("Shift-JIS"));
								sw.WriteLine(log);
								break;
							}
							catch (Exception)
							{
								await Task.Delay(1000);
							}
						}
					}
					catch (Exception)
					{
					}
				}
			});
		}

		/// <summary>
		/// TCPポートのオープン
		/// </summary>
		/// <param name="ipAddress">TCPサーバのIPアドレス</param>
		/// <param name="port">TCPサーバが使用するポート番号</param>
		/// <param name="listen">最大接続数</param>
		/// <param name="buffersize">受信バッファのサイズ</param>
		/// <returns></returns>
		internal bool Open(string ipAddress, int port, int listen, int buffersize)
		{
			// まだポートがオープンしけなければ処理
			if (isOpen == false)
			{
				bufferSize = buffersize;
				_ = AllDone.Set();
				// 指定されたIPアドレスが正しい値かチェック
				if (IPAddress.TryParse(ipAddress, out IPAddress result) == true)
				{
					IP = result;
				}
				else
				{
					if (isLog == true)
						message("IPアドレス文字列が不適切です");
					if (isMessage == true)
						_ = MessageBox.Show("IPアドレス文字列が不適切です", "TCP Server オープン", MessageBoxButton.OK, MessageBoxImage.Error);
					return false;
				}

				// 引数のIPアドレスがPCに存在しているか確認(127.0.0.1は除く)
				if (ipAddress != "127.0.0.1")
				{
					if (new List<IPAddress>(Dns.GetHostAddresses(Dns.GetHostName())).ConvertAll(x => x.ToString()).Any(l => l == ipAddress) == false)
					{
						if (isLog == true)
							message("指定されたIPアドレスは存在しません。");
						if (isMessage == true)
							_ = MessageBox.Show("指定されたIPアドレスは存在しません。", "TCP Server オープン", MessageBoxButton.OK, MessageBoxImage.Error);
						return false;
					}
				}

				Sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				Sock.Bind(new IPEndPoint(IP, port));
				Sock.Listen(listen);

				isOpen = true;
				accept();
			}

			return true;
		}

		/// <summary>
		/// 接続待機を別タスクで実行
		/// </summary>
		private void accept()
		{
			_ = Task.Run(() =>
			{
				while (true)
				{
					_ = AllDone.Reset();
					try
					{
						// 受信接続の試行を受け入れる非同期操作を開始
						_ = Sock.BeginAccept(new AsyncCallback(acceptCallback), Sock);
					}
					catch (ObjectDisposedException)
					{
						// オブジェクトが閉じられていれば終了
						break;
					}
					catch (Exception e)
					{
						if (isLog == true)
						{
							message(e.Message);
							continue;
						}
					}
					_ = AllDone.WaitOne();
				}
			});
		}

		/// <summary>
		/// 接続受け付けのコールバックメソッド
		/// </summary>
		/// <param name="asyncResult"></param>
		private void acceptCallback(IAsyncResult asyncResult)
		{
			// 待機スレッドが進行するようにシグナルをセット
			_ = AllDone.Set();
			// StateObjectを作成しソケットを取得
			StateObject state = new StateObject(bufferSize);
			try
			{
				state.ClientSocket = ((Socket)asyncResult.AsyncState).EndAccept(asyncResult);
			}
			catch (ObjectDisposedException)
			{
				return;
			}
			catch (Exception e)
			{
				if (isLog == true)
					message(e.Message);
				return;
			}

			// 接続中のクライアントを追加
			ClientSockets.Add(state.ClientSocket);
			// 受信時のコードバック処理を設定
			_ = state.ClientSocket.BeginReceive(state.Buffer, 0, bufferSize, 0, new AsyncCallback(readCallback), state);
		}

		/// <summary>
		/// TCP非同期受信が完了した時に呼び出されるメソッド
		/// これは接続してきたクライアント毎に生成される
		/// </summary>
		/// <param name="asyncResult"></param>
		private void readCallback(IAsyncResult asyncResult)
		{
			// acceptCallbackで生成されたStateObjectインスタンスを取得
			StateObject state = (StateObject)asyncResult.AsyncState;
			// クライアントソケットから受信データを取得
			try
			{
				// 受信サイズが0以上なら何かしらデータが送られてきたので処理を行う
				if (state.ClientSocket.EndReceive(asyncResult) > 0)
				{
					// TCPで受信したデータは「state.Buffer」にあるのでstring文字列に変換しつつ残バッファの分だけ不要な0があるので削除
					string receivestr = Encoding.GetEncoding("shift_jis").GetString(state.Buffer).TrimEnd('\0');

					/***** ここに受信したデータに対する処理を記述する *****/
					// 外部メソッドへ受信文字列を引数として渡し、string型の戻り値を送信データByte配列に変換
					byte[] senddata = Encoding.GetEncoding("shift_jis").GetBytes(Method(receivestr));
					// クライアントに非同期送信
					_ = state.ClientSocket.BeginSend(senddata, 0, senddata.Length, 0, new AsyncCallback(writeCallback), state);
				}
				else
				{
					// 受信サイズが0の場合は切断(相手が切断した)
					state.ClientSocket.Close();
					_ = ClientSockets.Remove(state.ClientSocket);
				}
			}
			catch (SocketException)
			{
				// 強制的に切断された
				state.ClientSocket.Close();
				_ = ClientSockets.Remove(state.ClientSocket);
			}
			catch (Exception e)
			{
				if (isLog == true)
					message(e.Message);
			}
		}

		/// <summary>
		/// TCP非同期送信が完了した時に呼び出されるメソッド
		/// </summary>
		/// <param name="asyncResult"></param>
		private void writeCallback(IAsyncResult asyncResult)
		{
			try
			{
				StateObject state = (StateObject)asyncResult.AsyncState;
				// リモートデバイスへのデータ送信を完了
				_ = state.ClientSocket.EndSend(asyncResult);
				// 受信時のコードバック処理を設定
				_ = state.ClientSocket.BeginReceive(state.Buffer, 0, bufferSize, 0, new AsyncCallback(readCallback), state);
			}
			catch (Exception e)
			{
				if (isLog == true)
					message(e.Message);
			}
		}

		/// <summary>
		/// ソケットを閉じる
		/// </summary>
		internal void Close()
		{
			// 接続されているTCPクライアントがあれば切断する
			foreach (Socket Cl in ClientSockets)
			{
				Cl.Shutdown(SocketShutdown.Both);
				Cl.Close();
			}
			ClientSockets.Clear();
			Sock?.Close();
			isOpen = false;
		}

		/// <summary>
		/// Socketのハンドラと使用する入出力バッファ
		/// </summary>
		private class StateObject
		{
			internal Socket ClientSocket { get; set; }
			internal byte[] Buffer;

			internal StateObject(int buffersize)
			{
				Buffer = new byte[buffersize];
			}
		}
	}
}
