enum AppEnvironment {
  dev('dev'),
  staging('staging'),
  prod('prod');

  const AppEnvironment(this.value);

  final String value;

  static AppEnvironment parse(String? raw) {
    return AppEnvironment.values.firstWhere(
      (env) => env.value == raw,
      orElse: () => AppEnvironment.dev,
    );
  }
}

