﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Requestrr.WebApi.Requestrr.Movies;
using Requestrr.WebApi.Requestrr.Notifications;

namespace Requestrr.WebApi.Requestrr.ChatClients
{
    public class DiscordMovieRequestingWorkFlow : RequestrrModuleBase<SocketCommandContext>, IMovieUserInterface
    {
        private IMovieSearcher _movieSearcher;
        private readonly IMovieRequester _movieRequester;
        private readonly MovieNotificationsRepository _notificationRequestRepository;
        private IUserMessage _lastCommandMessage;
        private readonly DiscordSettings _discordSettings;

        public DiscordMovieRequestingWorkFlow(
            SocketCommandContext context,
            DiscordSocketClient discord,
            IMovieSearcher movieSearcher,
            IMovieRequester movieRequester,
            DiscordSettingsProvider discordSettingsProvider,
            MovieNotificationsRepository notificationRequestRepository)
                : base(discord, context)
        {
            _movieSearcher = movieSearcher;
            _movieRequester = movieRequester;
            _notificationRequestRepository = notificationRequestRepository;
            _discordSettings = discordSettingsProvider.Provide();
        }

        public Task HandleMovieRequestAsync(string movieName)
        {
            if (!_discordSettings.EnableDirectMessageSupport && this.Context.Guild == null)
            {
                return ReplyToUserAsync($"This command is only available within a server.");
            }
            else if (this.Context.Guild != null && _discordSettings.MonitoredChannels.Any() && _discordSettings.MonitoredChannels.All(c => !Context.Message.Channel.Name.Equals(c, StringComparison.InvariantCultureIgnoreCase)))
            {
                return Task.CompletedTask;
            }

            if (string.IsNullOrWhiteSpace(movieName))
                return ReplyToUserAsync($"Here's how to use the movie command! You must type ```!movie``` followed by the name of the movie you're looking for, like this ```!movie putnamehere```");

            var workFlow = new MovieRequestingWorkflow(new MovieUserRequester(this.Context.Message.Author.Id.ToString(), this.Context.Message.Author.Username), _movieSearcher, _movieRequester, this, _notificationRequestRepository);
            return workFlow.HandleMovieRequestAsync(movieName);
        }

        public Task WarnNoMovieFoundAsync(string movieName)
        {
            return ReplyToUserAsync($"I could not find any movie with the name \"{movieName}\", please try something different.");
        }

        public async Task<MovieSelection> GetMovieSelectionAsync(IReadOnlyList<Movie> movies)
        {
            var fieldContent = string.Empty;
            var arrayMovies = movies.ToArray();

            for (int i = 0; i < movies.Take(10).Count(); i++)
            {
                fieldContent += $"{i + 1}) {arrayMovies[i].Title} ";

                if (!string.IsNullOrWhiteSpace(arrayMovies[i].ReleaseDate))
                    fieldContent += $"({arrayMovies[i].ReleaseDate.Substring(0, 4)}) ";

                fieldContent += $"[[TheMovieDb](https://www.themoviedb.org/movie/{arrayMovies[i].TheMovieDbId})]";
                fieldContent += System.Environment.NewLine;
            }

            var embedBuilder = new EmbedBuilder()
                .WithTitle("Movie Search")
                .WithDescription("Please select one of the search results. To abort answer **cancel**")
                .AddField("__Search Results__", fieldContent)
                .WithThumbnailUrl("https://i.imgur.com/44ueTES.png");

            var reply = await ReplyAsync(string.Empty, false, embedBuilder.Build());

            var selectionMessage = await NextMessageAsync(Context);
            var selectionMessageContent = selectionMessage?.Content ?? "cancel";

            if (selectionMessageContent.ToLower().StartsWith("cancel"))
            {
                await DeleteSafeAsync(reply);
                await DeleteSafeAsync(selectionMessage);
                await ReplyToUserAsync("Your request has been cancelled!!");

                return new MovieSelection
                {
                    IsCancelled = true
                };
            }
            else if (int.TryParse(selectionMessageContent, out var selectedMovie) && selectedMovie <= movies.Count)
            {
                await DeleteSafeAsync(reply);
                await DeleteSafeAsync(selectionMessage);
                return new MovieSelection
                {
                    Movie = movies[selectedMovie - 1]
                };
            }

            return new MovieSelection();
        }

        public Task WarnInvalidMovieSelectionAsync()
        {
            return ReplyToUserAsync("I didn't understand your dramatic babbling, I'm afraid you'll have to make a new request.");
        }

        public async Task DisplayMovieDetails(Movie movie)
        {
            var message = Context.Message;
            _lastCommandMessage = await ReplyAsync(string.Empty, false, GenerateMovieDetails(movie, message.Author));
        }

        public static Embed GenerateMovieDetails(Movie movie, SocketUser user)
        {
            var embedBuilder = new EmbedBuilder()
                .WithTitle($"{movie.Title} {(!string.IsNullOrWhiteSpace(movie.ReleaseDate) ? $"({movie.ReleaseDate.Split("T")[0].Substring(0, 4)})" : string.Empty)}")
                .WithDescription(movie.Overview.Substring(0, Math.Min(movie.Overview.Length, 255)) + "(...)")
                .WithFooter(user.Username, $"https://cdn.discordapp.com/avatars/{user.Id.ToString()}/{user.AvatarId}.png")
                .WithTimestamp(DateTime.Now)
                .WithUrl($"https://www.themoviedb.org/movie/{movie.TheMovieDbId}")
                .WithThumbnailUrl("https://i.imgur.com/44ueTES.png");

            if (!string.IsNullOrEmpty(movie.PosterPath) && movie.PosterPath.StartsWith("http", StringComparison.InvariantCultureIgnoreCase)) embedBuilder.WithImageUrl(movie.PosterPath);
            if (!string.IsNullOrWhiteSpace(movie.Quality)) embedBuilder.AddField("__Quality__", $"{movie.Quality}p", true);
            if (!string.IsNullOrWhiteSpace(movie.PlexUrl)) embedBuilder.AddField("__Plex__", $"[Watch now]({movie.PlexUrl})", true);
            if (!string.IsNullOrWhiteSpace(movie.EmbyUrl)) embedBuilder.AddField("__Emby__", $"[Watch now]({movie.EmbyUrl})", true);

            return embedBuilder.Build();
        }

        public async Task<bool> GetMovieRequestAsync()
        {
            await _lastCommandMessage?.AddReactionAsync(new Emoji("⬇"));
            await ReplyToUserAsync("If you want to request this movie please click on the ⬇ reaction.");

            var reaction = await WaitForReactionAsync(Context, _lastCommandMessage, new Emoji("⬇"));

            return reaction != null;
        }

        public Task WarnMovieAlreadyAvailable()
        {
            return ReplyToUserAsync("This movie is already available, enjoy!");
        }

        public Task DisplayRequestSuccess(Movie movie)
        {
            return ReplyToUserAsync($"Your request for **{movie.Title}** was sent successfully, your movie should be available soon!");
        }

        public async Task<bool> AskForNotificationRequestAsync()
        {
            await _lastCommandMessage?.AddReactionAsync(new Emoji("📧"));
            await ReplyToUserAsync("This movie has already been requested, you can click on the 📧 reaction to be notified when it becomes available.");

            var reaction = await WaitForReactionAsync(Context, _lastCommandMessage, new Emoji("📧"));

            return reaction != null;
        }

        public Task DisplayNotificationSuccessAsync(Movie movie)
        {
            return ReplyToUserAsync($"You will now receive a notification as soon as **{movie.Title}** becomes available to watch.");
        }

        public Task WarnMovieUnavailableAndAlreadyHasNotification()
        {
            return ReplyToUserAsync("This movie has already been requested and you will be notified when it becomes available.");
        }
    }
}