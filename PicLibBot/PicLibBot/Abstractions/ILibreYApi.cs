using Refit;

namespace PicLibBot.Abstractions;

internal interface ILibreYApi
{
    /*
     see:
     https://github.com/Ahwxorg/librey
     https://github.com/hnhx/librex

     "q" is the keyword
     "p" is the result page (the first page is 0)
     "t" is the search type (0=text, 1=image, 2=video, 3=torrent, 4=tor)
     The results are going to be in JSON format.
     The API supports both POST and GET requests.
     */
    [Get("/api.php?q={query}&p={page}&t=1")]
    Task<string> ListImagesAsync(string query, int page, CancellationToken cancellationToken);
}
