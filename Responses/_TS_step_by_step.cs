using System;

using System.Threading; // for threads

using TradeLink.Common;
using TradeLink.API;
using System.ComponentModel;
using System.Collections.Generic;   // for List<type>, Dictionary<type,type> etc. http://social.msdn.microsoft.com/Forums/en/csharpgeneral/thread/30e42195-73ab-4ef4-bc91-62aa89896326

using Newtonsoft.Json;          // for JSON support. http://json.codeplex.com/releases/view/107620
using Newtonsoft.Json.Linq;

using TradeLink.AppKit; // for class ResponseParametersHolder
using NDesk.Options; // for cmd-line args parsing

using System.Text;      // these 2 are for rabbitmq  (system.text for Encoding())
using RabbitMQ.Client;  // 


// mongodb: as a minimum:
using MongoDB.Bson;
using MongoDB.Driver;   // http://docs.mongodb.org/ecosystem/tutorial/use-csharp-driver/
// Additionally, you will frequently add one or more of these using statements:
using MongoDB.Driver.Builders;
using MongoDB.Driver.GridFS;
using MongoDB.Driver.Linq;
using MongoDB.Bson.Serialization;
// http://docs.mongodb.org/ecosystem/tutorial/getting-started-with-csharp-driver/#getting-started-with-csharp-driver


using System; // for debug.writeline(...)
using System.Diagnostics; // or use debug listener: http://www.dotnetperls.com/debug


namespace Responses
{

    public class _TS_step_by_step : MessageBusableResponseTemplate      // class name starts with "_" just to make it easy to find in a list of responses...

    //public class _TS_step_by_step : ResponseTemplate      // class name starts with "_" just to make it easy to find in a list of responses...
    {

        System.IO.StreamWriter log_file = new System.IO.StreamWriter("C:\\tradelink.afterlife\\_logs\\Responses\\_ts_step_by_step.log");

        // dimon: external json files support:
        MongoDB.Bson.BsonDocument bson = null;
        TradeLink.AppKit.ResponseParametersHolder response_parameters_holder = null;

        // constructor always goes 1st
        string debug_message_from_constructor = ""; // need to store it and show on 1st tick, otherwise debug messages are wiped out when ticks start to arrive
        public _TS_step_by_step()
            : base()
        {
            System.Diagnostics.Debug.WriteLine("class _TS_step_by_step constructor entry point");
            string[] args = MyGlobals.args;     // extract main(args) from MyGlobals (we store main(args) in Kadina Program.cs, ASP, etc.)
            if (args == null)
            {
                throw new Exception("you forgot to set MyGlobals.args (in Kadina or asp or in whatever you use)");
            }

            string response_config = "";
            //string storage_service_config_jsonfile = "";
            string rabbitmq_config_jsonfile = "";

            var p = new OptionSet() {
                { "a|response-config=", "response json configuration file",
                 v => response_config = v },
                //{ "c|storage-service-config=", "path to storage_service json configuration file",
                //  v => storage_service_config_jsonfile = v},
                {"r|rabbitmq-config=", "path to rabbitmq json configuration file",
                  v => rabbitmq_config_jsonfile = v}
            };

            // parse  cmd-line args
            List<string> extra;
            try
            {
                extra = p.Parse(args);
            }
            catch (OptionException e)
            {
                System.Diagnostics.Debug.WriteLine(e.Message);
                System.Diagnostics.Debug.WriteLine("Try `--help' for more information.");
                return;
            }

            // get settings from json file
            if (response_config != "")
            {
                response_parameters_holder = new ResponseParametersHolder();
                response_parameters_holder.parse_json_file(response_config);
                bson = response_parameters_holder.bson;

                //_ema_bar = Convert.ToInt32(bson["_ema_bar"]);
                _ema_bar = BsonSerializer.Deserialize<int>(bson["_ema_bar"].ToJson());
                _response_barsize_s = BsonSerializer.Deserialize<int>(bson["_response_barsize_s"].ToJson());
                _stop_k = BsonSerializer.Deserialize<double>(bson["_stop_k"].ToJson());
                _target_price_k = BsonSerializer.Deserialize<double>(bson["_target_price_k"].ToJson());

                //_target_price_k = Convert.ToDouble(bson["_target_price_k"]);

                // Custom name of response set by you
                Name = BsonSerializer.Deserialize<string>(bson["name"].ToJson());

                //debug_message_from_constructor = "parsed json file - OK (set slow_ma=" + _slow_ma_bar + " fast_ma=" + _fast_ma_bar;
                //D(debug_message_from_constructor); // wtf? why this message never showed up? seems messages are cleaned right before 1st GotTick();

                //ResponseParametersHolder storage_service_parameters_holder = new ResponseParametersHolder();
                //storage_service_parameters_holder.parse_json_file(storage_service_config_jsonfile);
                //storage_service_parameters_bson = storage_service_parameters_holder.bson;

                ResponseParametersHolder rabbitmq_parameters_holder = new ResponseParametersHolder();
                rabbitmq_parameters_holder.parse_json_file(rabbitmq_config_jsonfile);
                rabbitmq_parameters_bson = rabbitmq_parameters_holder.bson;
                call_me_from_child_constructor();
            }


            // track_symbols_NewTxt() called when new text label is added
            track_symbols.NewTxt += new TextIdxDelegate(track_symbols_NewTxt);

            //     Names of the indicators used by your response.  Length must correspond to
            //     actual indicator values send with SendIndicators event
            Indicators = GenericTracker.GetIndicatorNames(gens());

            //[_response_barsize_s, 22]
            track_barlists = new BarListTracker(new int[] { _response_barsize_s, 22 }, new BarInterval[] { BarInterval.CustomTime, BarInterval.CustomTime });
            track_barlists.GotNewBar += new SymBarIntervalDelegate(GotNewBar);
        }


        // after constructor we list all the trackers (standard and generic ones)
        //
        GenericTracker<bool> track_symbols = new GenericTracker<bool>("symbol");    // track_symbols is our sort of PRIMARY thing
        PositionTracker track_positions = new PositionTracker("position");
        TickTracker track_ticks = new TickTracker();
        BarListTracker track_barlists = null; // we can init it only after we parse bar interval parameter...  // new BarListTracker(BarInterval.Minute);
        // track MA with "history" in a list of decimals (separate list per each symbol)
        GenericTracker<List<decimal>> track_ema = new GenericTracker<List<decimal>>();

        // keep track of time for use in other functions
        int time = 0;

        GenericTrackerI[] gens()
        {
            return gt.geninds(track_symbols, track_positions);
        }

        void track_symbols_NewTxt(string txt, int idx)
        {
            // index all the trackers we're using
            track_positions.addindex(txt);
            track_ema.addindex(txt, new List<decimal>());
        }

        void GotNewBar(string symbol, int interval)
        {
            // get current barlist for this symbol+interval
            BarList bl = track_barlists[symbol, interval];

            // issue an event (todo: should I put this functionality into MessageResponseTemplate.cs? or should I get rid of GotNewBar at all? hmm...) will stay here for a while..
            BsonDocument bson_doc = new BsonDocument();
            bson_doc = new BsonDocument();
            bson_doc["Symbol"] = bl.RecentBar.Symbol;
            bson_doc["High"] = bl.RecentBar.High.ToString();
            bson_doc["Low"] = bl.RecentBar.Low.ToString();
            bson_doc["Open"] = bl.RecentBar.Open.ToString();
            bson_doc["Close"] = bl.RecentBar.Close.ToString();
            bson_doc["Volume"] = bl.RecentBar.Volume.ToString();
            bson_doc["isNew"] = bl.RecentBar.isNew;
            bson_doc["Bartime"] = bl.RecentBar.Bartime;
            bson_doc["Bardate"] = bl.RecentBar.Bardate;
            bson_doc["isValid"] = bl.RecentBar.isValid;
            bson_doc["Interval"] = bl.RecentBar.Interval;
            bson_doc["time"] = bl.RecentBar.time;

            //send_event(MimeType.got_new_bar, "bar", bson_doc.ToString());
            log_file.WriteLine("got_new_bar"+ bson_doc.ToString());

            // get index for symbol
            int idx = track_symbols.getindex(symbol);

            string dbg_msg = "GotNewBar(" + symbol + ", " + interval + "):";

            // check for first cross on first interval
            if (interval == _response_barsize_s)
            {
                decimal ema = Calc.Avg(Calc.EndSlice(bl.Close(), _ema_bar));

                track_ema[symbol].Add(ema);

                // drawings...
                if (bl.Close().Length > _ema_bar)
                {
                    // draw 2 sma lines:
                    sendchartlabel(ema, time, System.Drawing.Color.FromArgb(0xff, 0x01, 0x01));

                    // do the trade (if required)
                    decimal[] ema_arr = track_ema[symbol].ToArray();

                    decimal prev_ema = ema_arr[ema_arr.Length - 2];
                    decimal curr_ema = ema_arr[ema_arr.Length - 1];
                    decimal delta_ema = curr_ema - prev_ema;

                    // sma just crossed?
                    bool should_buy = track_positions[symbol].isFlat && delta_ema >= 0.002m;
                    bool should_sell = false;

                    dbg_msg += " delta_ema=" + delta_ema.ToString("000.000");
                    /*
                    dbg_msg += " fast=" + curr_sma_fast.ToString("000.000");
                    dbg_msg += " pr_slow=" + prev_sma_slow.ToString("000.000");
                    dbg_msg += " pr_fast=" + prev_sma_fast.ToString("000.000");
                    dbg_msg += " [" + symbol + "].isFlat=" + track_positions[symbol].isFlat.ToString();
                    dbg_msg += " track_positions[symbol].ToString=" + track_positions[symbol].ToString();
                    dbg_msg += " should_buy=" + should_buy.ToString();
                    dbg_msg += " should_sell=" + should_sell.ToString();
                     */

                    //senddebug("GotNewBar(): " + debug_position_tracker(symbol));

                    if (should_buy)
                    {
                        // come buy some! (c) Duke Nukem
                        string comment = " BuyMarket(" + symbol + ", " + EntrySize.ToString() + ")";
                        sendorder(new BuyMarket(symbol, EntrySize, comment));
                        log_file.WriteLine("close position: " + comment);
                        dbg_msg += comment;
                    }

                    if (false) // we do all the selling on tick()
                    {
                        if (!track_positions[symbol].isFlat && should_sell) // we don't short, so also check if !flat
                        //if ( should_sell) // we don't short, so also check if !flat
                        {
                            sendorder(new SellMarket(symbol, EntrySize));
                            dbg_msg += " SellMarket(" + symbol + ", " + EntrySize.ToString() + ")";
                        }
                    }
                }
            }
            //else
            //{
            //    // 
            //    dbg_msg += "GotNewBar() other interval=" + interval;
            //}

            // spit out one dbg message line per bar
            D(dbg_msg);

            // nice way to notify of current tracker values
            sendindicators(gt.GetIndicatorValues(idx, gens()));
            return;
        }




        public override void GotOrder(Order o)
        {
            base.GotOrder(o);
        }

        public override void GotOrderCancel(long id)
        {
            base.GotOrderCancel(id);
        }

        public override void GotMessage(MessageTypes type, long source, long dest, long msgid, string request, ref string response)
        {
            base.GotMessage(type, source, dest, msgid, request, ref response);
        }





        // GotTick is called everytime a new quote or trade occurs
        public override void GotTick(TradeLink.API.Tick tick)
        {
            base.GotTick(tick);

            // keep track of time from tick
            time = tick.time;

            // ignore quotes
            if (!tick.isTrade) return;

            // ignore ticks with timestamp prior to 9:30:00am
            if (tick.time < 93000) return;

            // --------------------------------------------------- rabbitmq begin -----------
            log_file.WriteLine(JsonConvert.SerializeObject(tick, Formatting.Indented));    // write all ticks into external file (to get a feeling on size)
            if (tick.time == 93205)
            {
                ;
            }
            if (false)
                if (tick.time > 93000 && tick.time < 93500)
                {
                    string rabbit_serverAddress = "amqp://localhost/";
                    string rabbit_exchange = "exch";
                    string rabbit_exchangeType = "fanout";
                    string rabbit_routingKey = "rout";
                    string rabbit_message = JsonConvert.SerializeObject(tick, Formatting.Indented);

                    ConnectionFactory rabbit_cf = new ConnectionFactory();
                    rabbit_cf.Uri = rabbit_serverAddress;
                    IConnection rabbit_conn = rabbit_cf.CreateConnection();
                    IModel rabbit_ch = rabbit_conn.CreateModel();
                    rabbit_ch.ExchangeDeclare(rabbit_exchange, rabbit_exchangeType);
                    IBasicProperties msg_props = rabbit_ch.CreateBasicProperties();
                    msg_props.ContentType = "text/plain";
                    rabbit_ch.BasicPublish(rabbit_exchange,
                                            rabbit_routingKey,
                                            msg_props,
                                            Encoding.UTF8.GetBytes(rabbit_message));    // or Encoding.UTF8.GetBytes();  - we convert message into a byte array 
                }
            // --------------------------------------------------- rabbitmq end -----------

            // ensure we track this symbol, all the other trackers will be indexed inside track_symbols_NewTxt()
            track_symbols.addindex(tick.symbol, false);

            // track tick
            track_ticks.newTick(tick);

            // track (custom) bars
            track_barlists.newTick(tick); // dimon: give any ticks (trades) to this symbol and tracker will create barlists automatically

            // check if need to exit position:
            log_file.WriteLine("check if need to exit position: track_positions[tick.symbol].isLong=" + track_positions[tick.symbol].isLong.ToString());

            if (!track_positions[tick.symbol].isLong) // isFlat)        - we sell only if we have long positions
            {

                // should exit long position due to hit target?
                if (track_positions[tick.symbol].isLong)
                {
                    // time to exit long position?
                    bool should_exit = track_positions[tick.symbol].AvgPrice * (decimal)_target_price_k <= tick.trade;
                    if (should_exit)
                    {
                        string comment = "exit long position due to hit target";
                        sendorder(new SellMarket(tick.symbol, EntrySize, comment));
                        log_file.WriteLine("close position: " + comment);
                        return;
                    }
                }

                // should exit short position due to hit target?
                if (track_positions[tick.symbol].isShort)
                {
                    bool should_exit = track_positions[tick.symbol].AvgPrice * (decimal)_target_price_k >= tick.trade;
                    if (should_exit)
                    {
                        string comment = "exit short position due to hit target";
                        sendorder(new BuyMarket(tick.symbol, EntrySize, comment));
                        log_file.WriteLine("close position: " + comment);
                        return;
                    }
                }

                // should exit long position due to hit stop?
                if (track_positions[tick.symbol].isLong)
                {
                    bool should_exit = track_positions[tick.symbol].AvgPrice * (decimal)_stop_k >= tick.trade;
                    if (should_exit)
                    {
                        string comment = "exit long position due to hit stop";
                        sendorder(new SellMarket(tick.symbol, EntrySize, comment));
                        log_file.WriteLine("close position: " + comment);
                        return;
                    }
                }

                // should exit short position due to hit stop?
                if (track_positions[tick.symbol].isShort)
                {
                    bool should_exit = track_positions[tick.symbol].AvgPrice * (decimal)_target_price_k >= tick.trade;
                    if (should_exit)
                    {
                        string comment = "exit short position due to hit stop";
                        sendorder(new BuyMarket(tick.symbol, EntrySize, comment));
                        log_file.WriteLine("close position: " + comment);
                        return;
                    }
                }


            }
        }


        public override void GotFill(Trade fill)
        {
            base.GotFill(fill);

            // make sure every fill is tracked against a position
            track_positions.Adjust(fill);

            // chart fills
            sendchartlabel(fill.xprice, time, TradeImpl.ToChartLabel(fill), fill.side ? System.Drawing.Color.Green : System.Drawing.Color.Red);

            //senddebug("GotFill(): sym: " + fill.symbol + " size:" + fill.xsize + " price: " + fill.xprice + " time: " + fill.xtime + " side: " + fill.side + " id: " + fill.id);
        }

        public override void GotPosition(Position p)
        {
            base.GotPosition(p);

            // make sure every position set at strategy startup is tracked
            track_positions.Adjust(p);

            // do some logging
            string dbg_msg = "";
            dbg_msg += "GotPosition(): sym: " + p.symbol + " size:" + p.Size + " avg price: " + p.AvgPrice;

            // eto bilo ne horosho realizovano: todo: redo: :) senddebug(dbg_msg);
        }

        int _entrysize = -1;
        int _ema_bar = -1;
        int _response_barsize_s = -1;
        int EntrySize = 100;
        double _stop_k = -1;
        double _target_price_k = -1;
    }

}
