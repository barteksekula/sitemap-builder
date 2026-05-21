using interview_webscapper;

// no check if args[0] exists - will throw IndexOutOfRangeException with no helpful message
string url = args[0];

SitemapBuilder builder = new SitemapBuilder(url);
builder.Build();

